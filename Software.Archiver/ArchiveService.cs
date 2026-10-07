using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace Software.Archiver
{
	public sealed record ArchiveItem(string Name, bool IsDirectory, long Size, long PackedSize, DateTime? Modified)
	{
		public string DisplaySize => IsDirectory ? "<DIR>" : $"{Size:N0} B";
		public string DisplayPackedSize => IsDirectory ? "" : $"{PackedSize:N0} B";
	}

	public sealed record ArchiveProgress(string Message, int Percent);

	public static class ArchiveService
	{
		public const int MaximumEntries = 100_000;
		public const long MaximumExtractedBytes = 10L * 1024 * 1024 * 1024;

		public static ArchiveItem[] ReadArchive(string path, string? password = null, CancellationToken cancellationToken = default)
		{
			using var archive = ArchiveFactory.Open(path, new ReaderOptions { Password = password });
			return GetArchiveEntries(archive, path, cancellationToken).Select(entry =>
			{
				cancellationToken.ThrowIfCancellationRequested();
				return new ArchiveItem(entry.Key ?? "", entry.IsDirectory, entry.Size, entry.CompressedSize, entry.LastModifiedTime);
			}).OrderByDescending(entry => entry.IsDirectory).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray();
		}

		public static void CreateZip(IEnumerable<string> sources, string outputPath, CompressionLevel compression = CompressionLevel.Optimal,
			CancellationToken cancellationToken = default, IProgress<ArchiveProgress>? progress = null)
		{
			string output = Path.GetFullPath(outputPath);
			if (!Path.GetExtension(output).Equals(".zip", StringComparison.OrdinalIgnoreCase))
				throw new ArgumentException("New archives must use the .zip extension.");
			EnsureMissing(output);
			string parent = Path.GetDirectoryName(output)!;
			if (!Directory.Exists(parent))
				throw new DirectoryNotFoundException("The output folder does not exist.");
			EnsureNoLinksOnPath(parent);

			var entries = new List<(string Path, string Name, bool Directory)>();
			var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (string source in sources)
			{
				cancellationToken.ThrowIfCancellationRequested();
				string fullSource = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
				bool directory = (File.GetAttributes(fullSource) & FileAttributes.Directory) != 0;
				if (fullSource.Equals(output, StringComparison.OrdinalIgnoreCase)
					|| (directory && output.StartsWith(fullSource + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
					throw new IOException("The output archive must be outside the selected folders and must not be an input file.");
				string rootName = Path.GetFileName(fullSource);
				if (rootName.Length == 0)
					throw new IOException("Add folders within a drive instead of the drive root.");
				AddSource(fullSource, rootName);
			}
			if (entries.Count == 0)
				throw new ArgumentException("Add at least one file or folder.");

			string temporary = Path.Combine(parent, $".Software.Archiver-{Guid.NewGuid():N}.tmp");
			try
			{
				using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
				using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
				{
					long bytes = 0;
					for (int index = 0; index < entries.Count; index++)
					{
						cancellationToken.ThrowIfCancellationRequested();
						var source = entries[index];
						var entry = zip.CreateEntry(source.Directory ? source.Name + "/" : source.Name, compression);
						DateTime modified = File.GetLastWriteTime(source.Path);
						if (modified.Year is >= 1980 and <= 2107)
							entry.LastWriteTime = new DateTimeOffset(modified);
						if (!source.Directory)
						{
							using var input = File.OpenRead(source.Path);
							using var destination = entry.Open();
							CopyStream(input, destination, cancellationToken, ref bytes, long.MaxValue);
						}
						progress?.Report(new ArchiveProgress($"Adding {source.Name}", (index + 1) * 100 / entries.Count));
					}
				}
				cancellationToken.ThrowIfCancellationRequested();
				File.Move(temporary, output);
			}
			finally
			{
				if (File.Exists(temporary))
					File.Delete(temporary);
			}

			void AddSource(string path, string name)
			{
				cancellationToken.ThrowIfCancellationRequested();
				FileAttributes attributes = File.GetAttributes(path);
				if ((attributes & FileAttributes.ReparsePoint) != 0)
					throw new IOException("Symbolic links and junctions cannot be added to an archive.");
				string normalized = ValidateEntryName(name);
				if (!names.Add(normalized))
					throw new IOException($"Multiple inputs would create the same archive entry: {normalized}");
				if (entries.Count >= MaximumEntries)
					throw new IOException($"An archive may contain at most {MaximumEntries:N0} entries.");
				bool directory = (attributes & FileAttributes.Directory) != 0;
				entries.Add((path, normalized, directory));
				if (directory)
				{
					foreach (string child in Directory.EnumerateFileSystemEntries(path))
						AddSource(child, normalized + "/" + Path.GetFileName(child));
				}
			}
		}

		public static void Extract(string archivePath, string destination, IEnumerable<string>? selectedNames = null,
			string? password = null, CancellationToken cancellationToken = default, IProgress<ArchiveProgress>? progress = null,
			long maximumBytes = MaximumExtractedBytes)
		{
			if (maximumBytes < 0)
				throw new ArgumentOutOfRangeException(nameof(maximumBytes));
			string root = Path.GetFullPath(destination);
			EnsureNoLinksOnPath(root);
			if (File.Exists(root))
				throw new IOException("The extraction destination is a file.");
			using var archive = ArchiveFactory.Open(archivePath, new ReaderOptions { Password = password });
			var allEntries = GetArchiveEntries(archive, archivePath, cancellationToken);
			var selected = selectedNames?.Select(ValidateEntryName).ToHashSet(StringComparer.Ordinal);
			var selectedDirectories = allEntries.Where(entry => entry.IsDirectory && selected != null
				&& selected.Contains(ValidateEntryName(entry.Key ?? ""))).Select(entry => ValidateEntryName(entry.Key!)).ToArray();
			var entries = allEntries.Where(entry => selected == null || selected.Contains(ValidateEntryName(entry.Key ?? ""))
				|| selectedDirectories.Any(directory => ValidateEntryName(entry.Key ?? "").StartsWith(directory + "/", StringComparison.Ordinal))).ToArray();
			if (entries.Length == 0)
				throw new InvalidDataException("There are no entries to extract.");

			var plan = new Dictionary<string, ExtractionEntry>(StringComparer.OrdinalIgnoreCase);
			long declaredBytes = 0;
			foreach (var entry in entries)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (!string.IsNullOrEmpty(entry.LinkTarget)
					|| (archive.Type == ArchiveType.Zip && entry.Attrib is int attributes && ((attributes & (int)FileAttributes.ReparsePoint) != 0
						|| (((uint)attributes >> 16) & 0xF000) == 0xA000)))
					throw new InvalidDataException("Archive links are not supported.");
				if (entry.IsEncrypted && string.IsNullOrEmpty(password))
					throw new InvalidDataException("This archive requires a password.");
				string name = ValidateEntryName(entry.Key ?? "");
				string target = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
				string prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
				if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
					throw new InvalidDataException("An archive entry points outside the extraction folder.");
				EnsureNoLinksOnPath(target);
				if (plan.TryGetValue(target, out var prior))
				{
					if (prior.IsDirectory && entry.IsDirectory)
						continue;
					throw new InvalidDataException($"Duplicate archive path: {name}");
				}
				if (entry.IsDirectory)
				{
					if (File.Exists(target))
						throw new IOException($"A file blocks an archive folder: {target}");
				}
				else
				{
					EnsureMissing(target);
					if (entry.Size < 0 || entry.Size > maximumBytes - declaredBytes)
						throw new InvalidDataException($"Extraction exceeds the {maximumBytes:N0}-byte safety limit.");
					declaredBytes += entry.Size;
				}
				plan.Add(target, entry);
			}

			foreach (string target in plan.Keys)
			{
				for (string? parent = Path.GetDirectoryName(target); parent != null && !parent.Equals(root, StringComparison.OrdinalIgnoreCase);
					parent = Path.GetDirectoryName(parent))
				{
					if (File.Exists(parent) || (plan.TryGetValue(parent, out var parentEntry) && !parentEntry.IsDirectory))
						throw new InvalidDataException($"A file blocks an archive folder: {parent}");
				}
			}

			cancellationToken.ThrowIfCancellationRequested();
			Directory.CreateDirectory(root);
			long extractedBytes = 0;
			int completed = 0;
			foreach (var (target, entry) in plan)
			{
				cancellationToken.ThrowIfCancellationRequested();
				EnsureNoLinksOnPath(target);
				if (entry.IsDirectory)
					Directory.CreateDirectory(target);
				else
				{
					Directory.CreateDirectory(Path.GetDirectoryName(target)!);
					bool created = false;
					try
					{
						using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
						{
							created = true;
							using var input = entry.OpenEntryStream();
							CopyStream(input, output, cancellationToken, ref extractedBytes, maximumBytes);
						}
						if (entry.LastModifiedTime is DateTime modified && modified.Year >= 1601)
							File.SetLastWriteTime(target, modified);
					}
					catch
					{
						if (created)
							File.Delete(target);
						throw;
					}
				}
				progress?.Report(new ArchiveProgress($"Extracting {entry.Key}", ++completed * 100 / plan.Count));
			}
		}

		private sealed record ExtractionEntry(string? Key, bool IsDirectory, long Size, long CompressedSize,
			DateTime? LastModifiedTime, string? LinkTarget, bool IsEncrypted, int? Attrib, Func<Stream> OpenEntryStream);

		private static ExtractionEntry[] GetArchiveEntries(IArchive archive, string path, CancellationToken cancellationToken)
		{
			if (archive.Type != ArchiveType.Tar)
			{
				return GetEntries(archive).Select(entry => new ExtractionEntry(entry.Key, entry.IsDirectory, entry.Size,
					entry.CompressedSize, entry.LastModifiedTime, entry.LinkTarget, entry.IsEncrypted,
					archive.Type == ArchiveType.Zip ? entry.Attrib : null, entry.OpenEntryStream)).ToArray();
			}

			var entries = new List<ExtractionEntry>();
			using var stream = File.OpenRead(path);
			using var reader = new TarReader(stream);
			int count = 0;
			TarEntry? entry;
			while ((entry = reader.GetNextEntry()) != null)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (++count > MaximumEntries)
					throw new InvalidDataException("The TAR archive exceeds the entry safety limit.");
				if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory))
					throw new InvalidDataException("TAR links and special filesystem entries are not supported.");
				bool directory = entry.EntryType == TarEntryType.Directory;
				if (directory && entry.Name.TrimEnd('/') == ".")
					continue;
				long offset = entry.DataOffset;
				long length = entry.Length;
				entries.Add(new ExtractionEntry(entry.Name, directory, length, length, entry.ModificationTime.LocalDateTime,
					null, false, null, () => new TarContentStream(path, offset, length)));
			}
			return entries.ToArray();
		}

		private sealed class TarContentStream : Stream
		{
			private readonly FileStream source;
			private long remaining;

			public TarContentStream(string path, long offset, long length)
			{
				source = File.OpenRead(path);
				source.Position = offset;
				remaining = length;
			}

			public override bool CanRead => true;
			public override bool CanSeek => false;
			public override bool CanWrite => false;
			public override long Length => throw new NotSupportedException();
			public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
			public override void Flush() => throw new NotSupportedException();
			public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
			public override void SetLength(long value) => throw new NotSupportedException();
			public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

			public override int Read(byte[] buffer, int offset, int count)
			{
				if (remaining == 0)
					return 0;
				int read = source.Read(buffer, offset, (int)Math.Min(count, remaining));
				if (read == 0)
					throw new EndOfStreamException("TAR entry data is incomplete.");
				remaining -= read;
				return read;
			}

			protected override void Dispose(bool disposing)
			{
				if (disposing)
					source.Dispose();
				base.Dispose(disposing);
			}
		}

		private static IArchiveEntry[] GetEntries(IArchive archive)
		{
			var entries = archive.Entries.Take(MaximumEntries + 1).ToArray();
			if (entries.Length > MaximumEntries)
				throw new InvalidDataException($"The archive exceeds the {MaximumEntries:N0}-entry safety limit.");
			return entries.Where(entry => !(entry.IsDirectory && entry.Key?.Replace('\\', '/').TrimEnd('/') == ".")).ToArray();
		}

		private static string ValidateEntryName(string name)
		{
			string normalized = name.Replace('\\', '/').TrimEnd('/');
			while (normalized.StartsWith("./", StringComparison.Ordinal))
				normalized = normalized[2..];
			if (normalized.Length == 0 || normalized.StartsWith('/') || Path.IsPathRooted(normalized))
				throw new InvalidDataException($"Unsafe archive path: {name}");
			string[] parts = normalized.Split('/');
			foreach (string part in parts)
			{
				string stem = part.Split('.')[0].ToUpperInvariant();
				bool reserved = stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
					|| (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9');
				if (part.Length == 0 || part is "." or ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
					|| part.EndsWith('.') || part.EndsWith(' ') || reserved)
					throw new InvalidDataException($"Unsafe archive path: {name}");
			}
			return normalized;
		}

		private static void EnsureNoLinksOnPath(string path)
		{
			for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
			{
				try
				{
					if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
						throw new IOException("Archive operations through symbolic links or junctions are not supported.");
				}
				catch (FileNotFoundException) { }
				catch (DirectoryNotFoundException) { }
			}
		}

		private static void EnsureMissing(string path)
		{
			if (File.Exists(path) || Directory.Exists(path))
				throw new IOException($"An item already exists: {path}. Existing items are never overwritten.");
		}

		private static void CopyStream(Stream input, Stream output, CancellationToken cancellationToken, ref long totalBytes, long maximumBytes)
		{
			byte[] buffer = new byte[81920];
			int count;
			while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (count > maximumBytes - totalBytes)
					throw new InvalidDataException($"Extraction exceeds the {maximumBytes:N0}-byte safety limit.");
				output.Write(buffer, 0, count);
				totalBytes += count;
			}
		}
	}
}