using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Software.PasswordManager
{
	public partial class MainWindow : Window
	{
		private VaultSession? _session;
		private Guid? _selectedId;
		private bool _populating = true;
		private bool _draftDirty;
		private bool _busy;
		private bool _selectionGuard;
		private DateTimeOffset _lastActivity = DateTimeOffset.UtcNow;
		private readonly DispatcherTimer _idleTimer;
		private readonly DispatcherTimer _clipboardTimer;
		private uint _clipboardSequence;
		private bool _lockRequested;

		[DllImport("user32.dll")]
		private static extern uint GetClipboardSequenceNumber();

		public MainWindow()
		{
			InitializeComponent();
			_idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
			_idleTimer.Tick += Idle_Tick;
			_idleTimer.Start();
			_clipboardTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
			_clipboardTimer.Tick += (_, _) => ClearOwnedClipboard();
			SystemEvents.SessionSwitch += Session_Switch;
			_populating = false;
			UpdateState();
		}

		private SecureString? PromptPassword(bool creating)
		{
			var dialog = new MasterPasswordDialog(creating) { Owner = this };
			return dialog.ShowDialog() == true ? dialog.Result : null;
		}

		private async void NewVault_Click(object sender, RoutedEventArgs eventArgs)
		{
			if (_busy || !ConfirmLock())
			{
				return;
			}
			var file = new SaveFileDialog { Title = "Create encrypted vault", Filter = "Encrypted vault|*.vault", DefaultExt = ".vault", AddExtension = true, OverwritePrompt = true };
			if (file.ShowDialog(this) != true)
			{
				return;
			}
			using var password = PromptPassword(creating: true);
			if (password == null)
			{
				return;
			}
			await UnlockAsync(file.FileName, password, create: true);
		}

		private async void OpenVault_Click(object sender, RoutedEventArgs eventArgs)
		{
			if (_busy || !ConfirmLock())
			{
				return;
			}
			var file = new OpenFileDialog { Title = "Unlock encrypted vault", Filter = "Encrypted vault|*.vault|All files|*.*" };
			if (file.ShowDialog(this) != true)
			{
				return;
			}
			using var password = PromptPassword(creating: false);
			if (password != null)
			{
				await UnlockAsync(file.FileName, password);
			}
		}

		public async Task<bool> UnlockAsync(string path, SecureString password, bool create = false)
		{
			if (_busy)
			{
				return false;
			}
			SetBusy(true);
			StatusText.Text = create ? "Creating encrypted vault..." : "Unlocking...";
			try
			{
				var opened = await Task.Run(() =>
				{
					var session = create ? VaultSession.Create(path, password) : VaultSession.Open(path, password);
					if (create)
					{
						try
						{
							session.Save();
						}
						catch
						{
							session.Dispose();
							throw;
						}
					}
					return session;
				});
				ClearVault();
				_session = opened;
				_lastActivity = DateTimeOffset.UtcNow;
				RefreshEntries();
				StatusText.Text = "Vault unlocked.";
				return true;
			}
			catch (Exception exception)
			{
				StatusText.Text = exception.Message;
				return false;
			}
			finally
			{
				SetBusy(false);
			}
		}

		private void RefreshEntries(Guid? select = null, bool populateSelection = true)
		{
			_selectionGuard = true;
			if (_session == null)
			{
				EntryList.ItemsSource = null;
			}
			else
			{
				string filter = SearchBox.Text;
				var entries = _session.Entries.Where(entry => entry.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
					|| entry.Username.Contains(filter, StringComparison.OrdinalIgnoreCase) || entry.Url.Contains(filter, StringComparison.OrdinalIgnoreCase))
					.OrderBy(entry => entry.Title, StringComparer.OrdinalIgnoreCase).ToArray();
				EntryList.ItemsSource = entries;
				EntryList.SelectedItem = entries.FirstOrDefault(entry => entry.Id == select);
			}
			_selectionGuard = false;
			if (populateSelection && select != null && EntryList.SelectedItem is VaultEntry selected)
			{
				Populate(selected);
			}
			UpdateState();
		}

		private void Populate(VaultEntry? entry)
		{
			_populating = true;
			_selectedId = entry?.Id;
			TitleBox.Text = entry?.Title ?? "";
			UsernameBox.Text = entry?.Username ?? "";
			EntryPassword.Password = entry?.Password ?? "";
			UrlBox.Text = entry?.Url ?? "";
			NotesBox.Text = entry?.Notes ?? "";
			RevealPassword.IsChecked = false;
			RevealedPassword.Clear();
			ModifiedLabel.Text = entry == null ? "New entry" : entry.Modified.LocalDateTime.ToString("g");
			_draftDirty = false;
			_populating = false;
			DeleteEntryButton.IsEnabled = entry != null;
		}

		private bool ConfirmDraft() => !_draftDirty || MessageBox.Show(this, "Discard unsaved entry edits?", "Unsaved entry", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

		private void Entry_Changed(object sender, SelectionChangedEventArgs eventArgs)
		{
			if (_selectionGuard || _populating)
			{
				return;
			}
			if (!ConfirmDraft())
			{
				_selectionGuard = true;
				EntryList.SelectedItem = EntryList.Items.Cast<VaultEntry>().FirstOrDefault(entry => entry.Id == _selectedId);
				_selectionGuard = false;
				return;
			}
			Populate(EntryList.SelectedItem as VaultEntry);
		}

		private void AddEntry_Click(object sender, RoutedEventArgs eventArgs)
		{
			if (!ConfirmDraft())
			{
				return;
			}
			_selectionGuard = true;
			EntryList.SelectedItem = null;
			_selectionGuard = false;
			Populate(null);
			TitleBox.Focus();
		}

		private async void SaveEntry_Click(object sender, RoutedEventArgs eventArgs) => await SaveEntryAsync();

		public async Task<bool> SaveEntryAsync()
		{
			if (_session == null || _busy)
			{
				return false;
			}
			try
			{
				var entry = new VaultEntry(_selectedId ?? Guid.NewGuid(), TitleBox.Text.Trim(), UsernameBox.Text, EntryPassword.Password,
					UrlBox.Text, NotesBox.Text, DateTimeOffset.UtcNow);
				_session.Upsert(entry);
				_selectedId = entry.Id;
				bool saved = await SaveVaultAsync();
				if (_session != null)
				{
					_draftDirty = !saved;
					RefreshEntries(entry.Id);
					if (!saved)
					{
						_draftDirty = true;
					}
				}
				return saved;
			}
			catch (Exception exception)
			{
				StatusText.Text = exception.Message;
				return false;
			}
		}

		private async void SaveVault_Click(object sender, RoutedEventArgs eventArgs) => await SaveVaultAsync();

		private async Task<bool> SaveVaultAsync()
		{
			if (_session == null || _busy)
			{
				return false;
			}
			SetBusy(true);
			try
			{
				await Task.Run(_session.Save);
				StatusText.Text = "Encrypted vault saved.";
				return true;
			}
			catch (Exception exception)
			{
				StatusText.Text = exception.Message;
				return false;
			}
			finally
			{
				SetBusy(false);
			}
		}

		private async void DeleteEntry_Click(object sender, RoutedEventArgs eventArgs)
		{
			if (_session == null || _selectedId == null || MessageBox.Show(this, "Delete the selected entry?", "Delete entry", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
			{
				return;
			}
			_session.Delete(_selectedId.Value);
			await SaveVaultAsync();
			if (_session != null)
			{
				Populate(null);
				RefreshEntries();
			}
		}

		private async void ChangeMaster_Click(object sender, RoutedEventArgs eventArgs)
		{
			if (_session == null || _busy)
			{
				return;
			}
			using var password = PromptPassword(creating: true);
			if (password == null)
			{
				return;
			}
			SetBusy(true);
			try
			{
				await Task.Run(() => _session.ChangeMasterPassword(password));
				StatusText.Text = "Master password changed. Older vault copies still require their previous password.";
			}
			catch (Exception exception)
			{
				StatusText.Text = exception.Message;
			}
			finally
			{
				SetBusy(false);
			}
		}

		private void Search_Changed(object sender, TextChangedEventArgs eventArgs)
		{
			if (!_populating && _session != null)
			{
				RefreshEntries(_selectedId, populateSelection: false);
			}
		}
		private void Draft_Changed(object sender, TextChangedEventArgs eventArgs)
		{
			if (!_populating)
			{
				_draftDirty = true;
			}
		}
		private void Password_Changed(object sender, RoutedEventArgs eventArgs)
		{
			if (!_populating)
			{
				_draftDirty = true;
			}
			if (RevealPassword?.IsChecked == true)
			{
				RevealedPassword.Text = EntryPassword.Password;
			}
		}
		private void Reveal_Changed(object sender, RoutedEventArgs eventArgs)
		{
			bool reveal = RevealPassword.IsChecked == true;
			RevealedPassword.Text = reveal ? EntryPassword.Password : "";
			RevealedPassword.Visibility = reveal ? Visibility.Visible : Visibility.Collapsed;
			EntryPassword.Visibility = reveal ? Visibility.Collapsed : Visibility.Visible;
		}
		private void Generate_Click(object sender, RoutedEventArgs eventArgs) => EntryPassword.Password = PasswordGenerator.Generate();

		private void CopyPassword_Click(object sender, RoutedEventArgs eventArgs) => CopySecret(EntryPassword.Password);
		private void CopyUsername_Click(object sender, RoutedEventArgs eventArgs) => CopySecret(UsernameBox.Text);
		private void CopySecret(string value)
		{
			try
			{
				var data = new DataObject();
				data.SetData(DataFormats.UnicodeText, value);
				data.SetData("CanIncludeInClipboardHistory", BitConverter.GetBytes(0));
				data.SetData("CanUploadToCloudClipboard", BitConverter.GetBytes(0));
				Clipboard.SetDataObject(data, copy: true);
				_clipboardSequence = GetClipboardSequenceNumber();
				_clipboardTimer.Stop();
				_clipboardTimer.Start();
				StatusText.Text = "Copied. Clipboard expires in 30 seconds.";
			}
			catch (Exception)
			{
				StatusText.Text = "Clipboard is unavailable.";
			}
		}

		private void ClearOwnedClipboard()
		{
			_clipboardTimer.Stop();
			try
			{
				if (_clipboardSequence != 0 && GetClipboardSequenceNumber() == _clipboardSequence)
				{
					Clipboard.Clear();
				}
				_clipboardSequence = 0;
			}
			catch (Exception)
			{
				_clipboardTimer.Start();
			}
		}

		private bool ConfirmLock() => !(_session?.IsDirty == true || _draftDirty) || MessageBox.Show(this,
			"Discard unsaved entry/vault changes and lock?", "Unsaved changes", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
		private void Lock_Click(object sender, RoutedEventArgs eventArgs)
		{
			if (ConfirmLock())
			{
				LockVault();
			}
		}

		public void LockVault()
		{
			if (_busy)
			{
				_lockRequested = true;
				return;
			}
			ClearVault();
		}

		private void ClearVault()
		{
			ClearOwnedClipboard();
			_selectionGuard = true;
			EntryList.ItemsSource = null;
			_selectionGuard = false;
			Populate(null);
			_session?.Dispose();
			_session = null;
			_populating = true;
			SearchBox.Clear();
			_populating = false;
			VaultPath.Text = "Vault locked";
			VaultPath.ToolTip = null;
			StatusText.Text = "Locked";
			UpdateState();
		}

		private void Idle_Tick(object? sender, EventArgs eventArgs)
		{
			if (_session != null && DateTimeOffset.UtcNow - _lastActivity >= TimeSpan.FromMinutes(5))
			{
				LockVault();
			}
		}
		private void Session_Switch(object sender, SessionSwitchEventArgs eventArgs)
		{
			if (eventArgs.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
			{
				_ = Dispatcher.BeginInvoke(LockVault);
			}
		}
		private void Window_Activity(object sender, MouseButtonEventArgs eventArgs) => _lastActivity = DateTimeOffset.UtcNow;

		private void Window_PreviewKeyDown(object sender, KeyEventArgs eventArgs)
		{
			_lastActivity = DateTimeOffset.UtcNow;
			if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && eventArgs.Key == Key.L)
			{
				eventArgs.Handled = true;
				Lock_Click(sender, new RoutedEventArgs());
			}
		}

		private void UpdateState()
		{
			bool unlocked = _session != null;
			VaultWorkspace.IsEnabled = unlocked && !_busy;
			SaveVaultButton.IsEnabled = ChangeMasterButton.IsEnabled = LockButton.IsEnabled = unlocked && !_busy;
			if (unlocked)
			{
				VaultPath.Text = _session!.FilePath;
				VaultPath.ToolTip = _session.FilePath;
			}
			Title = "Password Manager" + (unlocked ? " - Unlocked" : " - Locked");
		}
		private void SetBusy(bool value)
		{
			_busy = value;
			NewVaultButton.IsEnabled = OpenVaultButton.IsEnabled = !value;
			BusyProgress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
			UpdateState();
			if (!value && _lockRequested)
			{
				_lockRequested = false;
				LockVault();
			}
		}
		private void Window_Closing(object? sender, CancelEventArgs eventArgs)
		{
			if (_busy || !ConfirmLock())
			{
				eventArgs.Cancel = true;
				return;
			}
			LockVault();
		}
		private void Window_Closed(object? sender, EventArgs eventArgs)
		{
			SystemEvents.SessionSwitch -= Session_Switch;
			_idleTimer.Stop();
			_clipboardTimer.Stop();
			_session?.Dispose();
		}
	}
}