using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Software.PasswordManager;

namespace Software.UnitTests
{
	public class PasswordVaultTests
	{
		private string _root = null!;
		private string _path = null!;
		[SetUp]
		public void Setup()
		{
			_root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"PasswordVaultTests-{Guid.NewGuid():N}")).FullName;
			_path = Path.Combine(_root, "test.vault");
		}
		[TearDown]
		public void TearDown() => Directory.Delete(_root, recursive: true);

		private static SecureString Password(string text = "test master password 123!")
		{
			var password = new SecureString();
			foreach (char character in text)
			{
				password.AppendChar(character);
			}
			password.MakeReadOnly();
			return password;
		}

		private static VaultEntry Entry() => new(Guid.NewGuid(), "private account title", "private username", "private secret password",
			"https://example.test", "private notes", DateTimeOffset.UtcNow);

		[Test]
		public void EntryStringRepresentationDoesNotExposeSecrets()
		{
			var entry = Entry();
			Assert.That(entry.ToString(), Is.EqualTo(entry.Title));
			Assert.That(entry.ToString(), Does.Not.Contain(entry.Password).And.Not.Contain(entry.Notes));
		}

		[Test]
		public void EncryptedRoundTripPreservesEveryEntryFieldWithoutPlaintextSecretsOnDisk()
		{
			using var password = Password();
			var entry = Entry();
			using (var session = VaultSession.Create(_path, password))
			{
				session.Upsert(entry);
				session.Save();
				Assert.That(session.IsDirty, Is.False);
			}
			string raw = Encoding.UTF8.GetString(File.ReadAllBytes(_path));
			Assert.That(raw, Does.Not.Contain(entry.Title).And.Not.Contain(entry.Username).And.Not.Contain(entry.Password).And.Not.Contain(entry.Notes));
			Assert.That(raw, Does.Not.Contain("test master password 123!"));
			using var opened = VaultSession.Open(_path, password);
			Assert.That(opened.Entries.Single(), Is.EqualTo(entry));
		}

		[Test]
		public void WrongPasswordAndCiphertextTamperingAreRejected()
		{
			using var password = Password();
			using var wrong = Password("different master password!");
			using (var session = VaultSession.Create(_path, password))
			{
				session.Upsert(Entry());
				session.Save();
			}
			Assert.Throws<CryptographicException>(() => VaultSession.Open(_path, wrong));
			byte[] file = File.ReadAllBytes(_path);
			file[^1] ^= 1;
			File.WriteAllBytes(_path, file);
			Assert.Throws<CryptographicException>(() => VaultSession.Open(_path, password));
		}

		[Test]
		public void EachSaveUsesFreshAuthenticatedEncryption()
		{
			using var password = Password();
			using var session = VaultSession.Create(_path, password);
			session.Save();
			byte[] first = File.ReadAllBytes(_path);
			session.Save();
			Assert.That(File.ReadAllBytes(_path), Is.Not.EqualTo(first));
			using var opened = VaultSession.Open(_path, password);
			Assert.That(opened.Entries, Is.Empty);
		}

		[Test]
		public void MasterPasswordChangeMakesTheOldPasswordUnusable()
		{
			using var oldPassword = Password();
			using var newPassword = Password("new master password 456!");
			using var session = VaultSession.Create(_path, oldPassword);
			session.Upsert(Entry());
			session.Save();
			session.ChangeMasterPassword(newPassword);
			Assert.Throws<CryptographicException>(() => VaultSession.Open(_path, oldPassword));
			using var reopened = VaultSession.Open(_path, newPassword);
			Assert.That(reopened.Entries, Has.Count.EqualTo(1));
		}

		[Test]
		public void ExternalChangesAreNeverOverwrittenAndEncryptedTemporaryFilesAreCleanedUp()
		{
			using var password = Password();
			using var session = VaultSession.Create(_path, password);
			session.Save();
			byte[] changed = File.ReadAllBytes(_path);
			changed[^1] ^= 1;
			File.WriteAllBytes(_path, changed);
			Assert.Throws<IOException>(session.Save);
			Assert.That(File.ReadAllBytes(_path), Is.EqualTo(changed));
			Assert.That(Directory.GetFiles(_root, ".Software.PasswordManager-*.tmp"), Is.Empty);
		}

		[Test]
		public void DisposalLocksTheSessionAndRejectsFurtherOperations()
		{
			using var password = Password();
			var session = VaultSession.Create(_path, password);
			session.Upsert(Entry());
			session.Dispose();
			Assert.Throws<ObjectDisposedException>(session.Save);
			Assert.Throws<ObjectDisposedException>(() => session.Entries.ToArray());
		}

		[Test]
		public void HeaderTamperingAndTruncationAreRejected()
		{
			using var password = Password();
			using (var session = VaultSession.Create(_path, password))
			{
				session.Save();
			}
			byte[] original = File.ReadAllBytes(_path);
			byte[] changed = original.ToArray();
			changed[12] ^= 1;
			File.WriteAllBytes(_path, changed);
			Assert.Throws<CryptographicException>(() => VaultSession.Open(_path, password));
			File.WriteAllBytes(_path, original[..20]);
			Assert.Throws<InvalidDataException>(() => VaultSession.Open(_path, password));
		}

		[Test]
		public void EntryChangesAndStrongMasterPasswordRulesAreEnforced()
		{
			using var password = Password();
			using var shortPassword = Password("short");
			Assert.Throws<ArgumentException>(() => VaultSession.Create(_path, shortPassword));
			using var session = VaultSession.Create(_path, password);
			var entry = Entry();
			session.Upsert(entry);
			session.Upsert(entry with { Title = "changed" });
			Assert.That(session.Entries, Has.Count.EqualTo(1));
			session.Delete(entry.Id);
			Assert.That(session.Entries, Is.Empty);
			Assert.That(VaultSession.PasswordsMatch(password, password), Is.True);
			Assert.That(VaultSession.PasswordsMatch(password, shortPassword), Is.False);
			Assert.That(PasswordGenerator.Generate(), Has.Length.EqualTo(24));
		}
	}

	[Apartment(ApartmentState.STA)]
	public class PasswordManagerUiTests
	{
		private MainWindow _window = null!;
		private string _root = null!;
		private string _path = null!;

		[SetUp]
		public void Setup()
		{
			_root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"PasswordManagerUiTests-{Guid.NewGuid():N}")).FullName;
			_path = Path.Combine(_root, "test.vault");
			_window = new MainWindow();
			_window.Show();
		}
		[TearDown]
		public void TearDown()
		{
			_window.LockVault();
			_window.Close();
			Directory.Delete(_root, recursive: true);
		}

		private static SecureString TestPassword()
		{
			var password = new SecureString();
			foreach (char character in "UI test master password 123!")
			{
				password.AppendChar(character);
			}
			password.MakeReadOnly();
			return password;
		}

		[Test]
		public void CreateSaveEntryLockAndReopenWorkWithoutRetainingUiSecrets()
		{
			RunOnDispatcher(async () =>
			{
				using var password = TestPassword();
				Assert.That(await _window.UnlockAsync(_path, password, create: true), Is.True);
				Assert.That(((Grid)_window.FindName("VaultWorkspace")).IsEnabled, Is.True);
				((TextBox)_window.FindName("TitleBox")).Text = "Example";
				((TextBox)_window.FindName("UsernameBox")).Text = "username";
				((PasswordBox)_window.FindName("EntryPassword")).Password = "dummy entry password";
				((TextBox)_window.FindName("NotesBox")).Text = "private note";
				Assert.That(await _window.SaveEntryAsync(), Is.True);
				Assert.That(((DataGrid)_window.FindName("EntryList")).Items.Count, Is.EqualTo(1));
				((CheckBox)_window.FindName("RevealPassword")).IsChecked = true;
				Assert.That(((TextBox)_window.FindName("RevealedPassword")).Text, Is.EqualTo("dummy entry password"));
				_window.LockVault();
				Assert.That(((Grid)_window.FindName("VaultWorkspace")).IsEnabled, Is.False);
				Assert.That(((DataGrid)_window.FindName("EntryList")).Items.Count, Is.EqualTo(0));
				Assert.That(((PasswordBox)_window.FindName("EntryPassword")).Password, Is.Empty);
				Assert.That(((TextBox)_window.FindName("RevealedPassword")).Text, Is.Empty);
				Assert.That(((TextBox)_window.FindName("NotesBox")).CanUndo, Is.False);
				Assert.That(await _window.UnlockAsync(_path, password), Is.True);
				Assert.That(((DataGrid)_window.FindName("EntryList")).Items.Count, Is.EqualTo(1));
			});
		}

		[Test]
		public void SearchKeepsUnsavedDraftEditsAndOnlyFiltersMetadata()
		{
			RunOnDispatcher(async () =>
			{
				using var password = TestPassword();
				Assert.That(await _window.UnlockAsync(_path, password, create: true), Is.True);
				((TextBox)_window.FindName("TitleBox")).Text = "Example";
				((PasswordBox)_window.FindName("EntryPassword")).Password = "dummy password";
				Assert.That(await _window.SaveEntryAsync(), Is.True);
				((TextBox)_window.FindName("NotesBox")).Text = "unsaved draft";
				((TextBox)_window.FindName("SearchBox")).Text = "Example";
				Assert.That(((TextBox)_window.FindName("NotesBox")).Text, Is.EqualTo("unsaved draft"));
				((TextBox)_window.FindName("SearchBox")).Text = "dummy password";
				Assert.That(((DataGrid)_window.FindName("EntryList")).Items.Count, Is.EqualTo(0));
				Assert.That(((TextBox)_window.FindName("NotesBox")).Text, Is.EqualTo("unsaved draft"));
			});
		}

		[Test]
		public void MasterPasswordDialogClearsBothPasswordControlsOnClose()
		{
			var dialog = new MasterPasswordDialog(creating: true) { Owner = _window };
			dialog.Show();
			((PasswordBox)dialog.FindName("MasterPassword")).Password = "dummy master password";
			((PasswordBox)dialog.FindName("Confirmation")).Password = "dummy master password";
			dialog.Close();
			Assert.That(((PasswordBox)dialog.FindName("MasterPassword")).Password, Is.Empty);
			Assert.That(((PasswordBox)dialog.FindName("Confirmation")).Password, Is.Empty);
		}

		private void RunOnDispatcher(Func<Task> action)
		{
			Task task = _window.Dispatcher.InvokeAsync(action).Task.Unwrap();
			var frame = new DispatcherFrame();
			var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
			timeout.Tick += (_, _) => frame.Continue = false;
			_ = task.ContinueWith(_ => _window.Dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
			timeout.Start();
			Dispatcher.PushFrame(frame);
			timeout.Stop();
			Assert.That(task.IsCompleted, Is.True, "Password Manager did not finish within 15 seconds.");
			task.GetAwaiter().GetResult();
		}
	}
}