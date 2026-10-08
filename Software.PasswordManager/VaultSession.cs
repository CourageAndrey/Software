using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;

namespace Software.PasswordManager
{
	public sealed record VaultEntry(Guid Id, string Title, string Username, string Password, string Url, string Notes, DateTimeOffset Modified)
	{
		public override string ToString() => Title;
	}

	public sealed class VaultSession : IDisposable
	{
		private const int _iterations = 600_000;
		private const int _headerLength = 60;
		private const int _tagLength = 16;
		private const int _maximumPayloadBytes = 2 * 1024 * 1024;
		private static readonly byte[] _magic = "SPVAULT1"u8.ToArray();
		private byte[] _key;
		private byte[] _salt;
		private string? _fileHash;
		private readonly List<VaultEntry> _entries;
		private bool _disposed;

		private sealed record Payload(int Version, List<VaultEntry> Entries);
		public string FilePath { get; }
		public bool IsDirty { get; private set; }
		public IReadOnlyList<VaultEntry> Entries
		{
			get
			{
				ThrowIfDisposed();
				return _entries.AsReadOnly();
			}
		}

		private VaultSession(string path, byte[] key, byte[] salt, List<VaultEntry> entries, string? fileHash)
		{
			FilePath = path;
			_key = key;
			_salt = salt;
			_entries = entries;
			_fileHash = fileHash;
		}

		public static VaultSession Create(string path, SecureString password)
		{
			if (password.Length < 12)
			{
				throw new ArgumentException("Use a master password of at least 12 characters.");
			}
			string fullPath = Path.GetFullPath(path);
			if (File.Exists(fullPath) || Directory.Exists(fullPath))
			{
				throw new IOException("Choose a new vault file. Existing files are never overwritten when creating a vault.");
			}
			byte[] salt = RandomNumberGenerator.GetBytes(32);
			return new VaultSession(fullPath, DeriveKey(password, salt), salt, [], null) { IsDirty = true };
		}

		public static VaultSession Open(string path, SecureString password)
		{
			string fullPath = Path.GetFullPath(path);
			using var stream = File.OpenRead(fullPath);
			if (stream.Length is < _headerLength + _tagLength or > _maximumPayloadBytes + _headerLength + _tagLength)
			{
				throw new InvalidDataException("Invalid or oversized vault file.");
			}
			byte[] file = new byte[checked((int)stream.Length)];
			stream.ReadExactly(file);
			if (!file.AsSpan(0, 8).SequenceEqual(_magic) || BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(8, 4)) != _iterations)
			{
				throw new InvalidDataException("Unsupported or damaged vault format.");
			}
			int length = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(56, 4));
			if (length < 0 || length > _maximumPayloadBytes || length != file.Length - _headerLength - _tagLength)
			{
				throw new InvalidDataException("Invalid vault content length.");
			}
			byte[] salt = file.AsSpan(12, 32).ToArray();
			byte[] key = DeriveKey(password, salt);
			byte[] plaintext = new byte[length];
			try
			{
				using var cipher = new AesGcm(key, _tagLength);
				cipher.Decrypt(file.AsSpan(44, 12), file.AsSpan(_headerLength + _tagLength), file.AsSpan(_headerLength, _tagLength), plaintext, file.AsSpan(0, _headerLength));
				var payload = JsonSerializer.Deserialize<Payload>(plaintext, new JsonSerializerOptions { MaxDepth = 16 })
					?? throw new InvalidDataException("Invalid vault data.");
				if (payload.Version != 1 || payload.Entries == null || payload.Entries.Count > 1000)
				{
					throw new InvalidDataException("Unsupported vault data.");
				}
				var ids = new HashSet<Guid>();
				foreach (var entry in payload.Entries)
				{
					ValidateEntry(entry);
					if (!ids.Add(entry.Id))
					{
						throw new InvalidDataException("Duplicate vault entry IDs.");
					}
				}
				return new VaultSession(fullPath, key, salt, payload.Entries, Convert.ToHexString(SHA256.HashData(file)));
			}
			catch (CryptographicException)
			{
				CryptographicOperations.ZeroMemory(key);
				throw new CryptographicException("The master password is incorrect or the vault was damaged or modified.");
			}
			catch
			{
				CryptographicOperations.ZeroMemory(key);
				throw;
			}
			finally
			{
				CryptographicOperations.ZeroMemory(plaintext);
			}
		}

		public void Upsert(VaultEntry entry)
		{
			ThrowIfDisposed();
			ValidateEntry(entry);
			int index = _entries.FindIndex(item => item.Id == entry.Id);
			if (index < 0)
			{
				if (_entries.Count >= 1000)
				{
					throw new InvalidOperationException("A vault may contain at most 1,000 entries.");
				}
				_entries.Add(entry);
			}
			else
			{
				_entries[index] = entry;
			}
			IsDirty = true;
		}

		public void Delete(Guid id)
		{
			ThrowIfDisposed();
			if (_entries.RemoveAll(entry => entry.Id == id) > 0)
			{
				IsDirty = true;
			}
		}

		public void Save()
		{
			ThrowIfDisposed();
			WriteEncrypted(_key, _salt);
		}

		public void ChangeMasterPassword(SecureString password)
		{
			ThrowIfDisposed();
			if (password.Length < 12)
			{
				throw new ArgumentException("Use a master password of at least 12 characters.");
			}
			byte[] salt = RandomNumberGenerator.GetBytes(32);
			byte[] key = DeriveKey(password, salt);
			try
			{
				WriteEncrypted(key, salt);
				CryptographicOperations.ZeroMemory(_key);
				_key = key;
				_salt = salt;
			}
			catch
			{
				CryptographicOperations.ZeroMemory(key);
				throw;
			}
		}

		private void WriteEncrypted(byte[] key, byte[] salt)
		{
			byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(new Payload(1, _entries));
			try
			{
				if (plaintext.Length > _maximumPayloadBytes)
				{
					throw new InvalidOperationException("The vault exceeds its 2 MiB content limit.");
				}
				byte[] file = new byte[_headerLength + _tagLength + plaintext.Length];
				_magic.CopyTo(file, 0);
				BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(8, 4), _iterations);
				salt.CopyTo(file, 12);
				RandomNumberGenerator.Fill(file.AsSpan(44, 12));
				BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(56, 4), plaintext.Length);
				using var cipher = new AesGcm(key, _tagLength);
				cipher.Encrypt(file.AsSpan(44, 12), plaintext, file.AsSpan(_headerLength + _tagLength), file.AsSpan(_headerLength, _tagLength), file.AsSpan(0, _headerLength));
				string temporary = Path.Combine(Path.GetDirectoryName(FilePath)!, $".Software.PasswordManager-{Guid.NewGuid():N}.tmp");
				try
				{
					using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
					{
						output.Write(file);
						output.Flush(flushToDisk: true);
					}
					if (_fileHash == null)
					{
						File.Move(temporary, FilePath);
					}
					else
					{
						using (var existing = File.OpenRead(FilePath))
						{
							if (!Convert.ToHexString(SHA256.HashData(existing)).Equals(_fileHash, StringComparison.Ordinal))
							{
								throw new IOException("The vault changed outside this application. Reopen it before saving; external changes were not overwritten.");
							}
						}
						File.Replace(temporary, FilePath, null);
					}
					_fileHash = Convert.ToHexString(SHA256.HashData(file));
					IsDirty = false;
				}
				finally
				{
					if (File.Exists(temporary))
					{
						File.Delete(temporary);
					}
				}
			}
			finally
			{
				CryptographicOperations.ZeroMemory(plaintext);
			}
		}

		private static byte[] DeriveKey(SecureString password, byte[] salt)
		{
			using var characters = new PasswordCharacters(password);
			byte[] key = new byte[32];
			Rfc2898DeriveBytes.Pbkdf2(characters.Value.AsSpan(), salt, key, _iterations, HashAlgorithmName.SHA256);
			return key;
		}

		public static bool PasswordsMatch(SecureString first, SecureString second)
		{
			using var left = new PasswordCharacters(first);
			using var right = new PasswordCharacters(second);
			return CryptographicOperations.FixedTimeEquals(MemoryMarshal.AsBytes(left.Value.AsSpan()), MemoryMarshal.AsBytes(right.Value.AsSpan()));
		}

		private sealed class PasswordCharacters : IDisposable
		{
			public char[] Value { get; }
			public PasswordCharacters(SecureString password)
			{
				Value = new char[password.Length];
				IntPtr pointer = Marshal.SecureStringToGlobalAllocUnicode(password);
				try
				{
					Marshal.Copy(pointer, Value, 0, Value.Length);
				}
				finally
				{
					Marshal.ZeroFreeGlobalAllocUnicode(pointer);
				}
			}
			public void Dispose() => CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(Value.AsSpan()));
		}

		private static void ValidateEntry(VaultEntry entry)
		{
			if (entry == null || entry.Id == Guid.Empty || string.IsNullOrWhiteSpace(entry.Title) || entry.Title.Length > 500
				|| entry.Username == null || entry.Username.Length > 2000 || entry.Password == null || entry.Password.Length > 16000
				|| entry.Url == null || entry.Url.Length > 4000 || entry.Notes == null || entry.Notes.Length > 20000)
			{
				throw new InvalidDataException("Entry fields are missing or exceed the supported limits.");
			}
		}

		private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}
			_disposed = true;
			CryptographicOperations.ZeroMemory(_key);
			_entries.Clear();
			_fileHash = null;
			IsDirty = false;
		}
	}

	public static class PasswordGenerator
	{
		public static string Generate(int length = 24)
		{
			if (length is < 12 or > 128)
			{
				throw new ArgumentOutOfRangeException(nameof(length), "Generate passwords of 12-128 characters.");
			}
			const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%&*+-_=?";
			char[] value = new char[length];
			for (int index = 0; index < length; index++)
			{
				value[index] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
			}
			return new string(value);
		}
	}
}