using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Software.FileCommander
{
	public partial class MainWindow : Window
	{
		private readonly string? _sessionFile;
		private TabbedPane _activeSide;
		private bool _busy;

		private FilePane ActivePane => _activeSide.ActivePane;
		private TabbedPane OtherSide => _activeSide == LeftPane ? RightPane : LeftPane;
		/// <summary>Completes when the saved tabs, or the user folder on first run, have been opened.</summary>
		public Task Initialization { get; private set; } = Task.CompletedTask;

		public MainWindow() : this(Path.Combine(AppContext.BaseDirectory, "tabs.txt"))
		{
		}

		/// <param name="sessionFile">Text file that keeps the open tabs between runs, or null to keep nothing.</param>
		public MainWindow(string? sessionFile)
		{
			_sessionFile = sessionFile;
			InitializeComponent();
			_activeSide = LeftPane;
			LeftPane.Activated += ActivateSide;
			RightPane.Activated += ActivateSide;
			LeftPane.CommandRequested += Pane_CommandRequested;
			RightPane.CommandRequested += Pane_CommandRequested;
			ActivateSide(LeftPane);
		}

		private async void Pane_CommandRequested(TabbedPane side, FileCommand command)
		{
			ActivateSide(side);
			if (command == FileCommand.Refresh)
			{
				await RefreshAllAsync();
			}
			else
			{
				await ExecuteAsync(command);
			}
		}

		private async void Window_Loaded(object sender, RoutedEventArgs eventArgs)
		{
			Initialization = RestoreTabsAsync();
			await Initialization;
		}

		private Task RestoreTabsAsync()
		{
			string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			var (left, right) = _sessionFile == null ? (null, null) : TabSession.Load(_sessionFile);
			return Task.WhenAll(Restore(LeftPane, left), Restore(RightPane, right));

			Task Restore(TabbedPane side, TabSession.Side? saved) =>
				saved is { Folders.Length: > 0 } ? side.RestoreAsync(saved.Folders, saved.ActiveIndex) : side.ActivePane.NavigateAsync(home);
		}

		private void ActivateSide(TabbedPane side)
		{
			_activeSide = side;
			LeftPane.IsActive = side == LeftPane;
			RightPane.IsActive = side == RightPane;
		}

		private Task RefreshAllAsync() => Task.WhenAll(LeftPane.Panes.Concat(RightPane.Panes).Select(pane => pane.RefreshAsync()));

		private void Window_Closing(object? sender, CancelEventArgs eventArgs)
		{
			if (_busy)
			{
				eventArgs.Cancel = true;
				OperationStatus.Text = "Wait for the current file operation to finish before closing.";
			}
			else if (_sessionFile != null && Initialization.IsCompleted)
			{
				// Closing while saved tabs are still opening keeps the previous file instead of saving half-opened tabs.
				TabSession.Save(_sessionFile, LeftPane.Session, RightPane.Session);
			}
		}

		private async void Command_Click(object sender, RoutedEventArgs eventArgs)
		{
			if (sender is Button { Tag: FileCommand command })
			{
				await ExecuteAsync(command);
			}
		}

		private async void Window_PreviewKeyDown(object sender, KeyEventArgs eventArgs)
		{
			if (_busy)
			{
				return;
			}

			if (await HandleTabShortcutAsync(eventArgs))
			{
				return;
			}

			if (Keyboard.Modifiers != ModifierKeys.None || Keyboard.FocusedElement is TextBox)
			{
				return;
			}

			FileCommand? command = eventArgs.Key switch
			{
				Key.F2 => FileCommand.Rename,
				Key.F5 => FileCommand.Copy,
				Key.F6 => FileCommand.Move,
				Key.F7 => FileCommand.NewFolder,
				Key.F8 or Key.Delete => FileCommand.Delete,
				_ => null
			};
			if (command != null)
			{
				eventArgs.Handled = true;
				await ExecuteAsync(command.Value);
			}
			else if (eventArgs.Key == Key.Tab && ActivePane.IsFileListFocused)
			{
				eventArgs.Handled = true;
				var other = OtherSide;
				ActivateSide(other);
				other.ActivePane.FocusList();
			}
		}

		/// <summary>Handles the Total Commander tab shortcuts: Ctrl+T, Ctrl+W, Ctrl+(Shift+)Tab and Ctrl+Up.</summary>
		private async Task<bool> HandleTabShortcutAsync(KeyEventArgs eventArgs)
		{
			var side = _activeSide;
			switch (Keyboard.Modifiers, eventArgs.Key)
			{
				case (ModifierKeys.Control, Key.T):
					eventArgs.Handled = true;
					await side.OpenTabAsync(side.ActivePane.CurrentPath);
					return true;
				case (ModifierKeys.Control, Key.W):
					eventArgs.Handled = true;
					side.CloseTab(side.ActivePane);
					return true;
				case (ModifierKeys.Control, Key.Tab):
				case (ModifierKeys.Control | ModifierKeys.Shift, Key.Tab):
					eventArgs.Handled = true;
					side.SelectAdjacentTab(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
					return true;
				case (ModifierKeys.Control, Key.Up) when side.ActivePane.IsFileListFocused && side.ActivePane.SelectedFolder is string folder:
					eventArgs.Handled = true;
					await side.OpenTabAsync(folder);
					return true;
				default:
					return false;
			}
		}

		private async Task ExecuteAsync(FileCommand command)
		{
			if (_busy || ActivePane.IsLoading || string.IsNullOrEmpty(ActivePane.CurrentPath))
			{
				return;
			}

			var sourcePane = ActivePane;
			var destinationPane = OtherSide.ActivePane;
			string sourceFolder = sourcePane.CurrentPath;
			string targetFolder = destinationPane.CurrentPath;
			string[] selected = sourcePane.SelectedPaths;
			if (command != FileCommand.NewFolder && selected.Length == 0)
			{
				OperationStatus.Text = "Select a file or folder first.";
				return;
			}

			Action operation;
			string success;
			switch (command)
			{
				case FileCommand.Copy:
				case FileCommand.Move:
					if (destinationPane.IsLoading || string.IsNullOrEmpty(targetFolder))
					{
						return;
					}

					if (MessageBox.Show(this, $"{command} {selected.Length} selected item(s) to:\n{targetFolder}?", command.ToString(),
						MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
					{
						return;
					}

					operation = command == FileCommand.Copy ? () => FileOperations.Copy(selected, targetFolder) : () => FileOperations.Move(selected, targetFolder);
					success = $"{command} completed: {selected.Length} item(s).";
					break;
				case FileCommand.Rename:
					if (selected.Length != 1)
					{
						OperationStatus.Text = "Select exactly one item to rename.";
						return;
					}
					string? name = PromptForName("Rename", Path.GetFileName(selected[0]));
					if (name == null)
					{
						return;
					}

					operation = () => FileOperations.Rename(selected[0], name);
					success = "Item renamed.";
					break;
				case FileCommand.NewFolder:
					string? folderName = PromptForName("New folder", "New folder");
					if (folderName == null)
					{
						return;
					}

					operation = () => FileOperations.CreateFolder(sourceFolder, folderName);
					success = "Folder created.";
					break;
				case FileCommand.Delete:
					if (MessageBox.Show(this, $"Send {selected.Length} selected item(s) to the Recycle Bin?", "Delete",
						MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
					{
						return;
					}

					operation = () => FileOperations.Recycle(selected);
					success = "Selected items sent to the Recycle Bin.";
					break;
				default:
					return;
			}

			_busy = true;
			PaneHost.IsEnabled = CommandBar.IsEnabled = false;
			BusyProgress.Visibility = Visibility.Visible;
			OperationStatus.Text = $"{command} in progress...";
			try
			{
				await Task.Run(operation);
				OperationStatus.Text = success;
			}
			catch (Exception exception)
			{
				OperationStatus.Text = $"{command} stopped. Some items may already have been processed.";
				MessageBox.Show(this, exception.Message, $"{command} failed", MessageBoxButton.OK, MessageBoxImage.Error);
			}
			finally
			{
				await RefreshAllAsync();
				_busy = false;
				PaneHost.IsEnabled = CommandBar.IsEnabled = true;
				BusyProgress.Visibility = Visibility.Collapsed;
				sourcePane.FocusList();
			}
		}

		private string? PromptForName(string title, string initialName)
		{
			var input = new TextBox { Text = initialName, Margin = new Thickness(0, 0, 0, 14), Padding = new Thickness(6) };
			var accept = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
			var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
			var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
			buttons.Children.Add(accept);
			buttons.Children.Add(cancel);
			var content = new StackPanel { Margin = new Thickness(18) };
			content.Children.Add(new TextBlock { Text = "Name", Margin = new Thickness(0, 0, 0, 6) });
			content.Children.Add(input);
			content.Children.Add(buttons);
			var dialog = new Window
			{
				Owner = this, Title = title, Content = content, Width = 420, SizeToContent = SizeToContent.Height,
				ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false
			};
			accept.Click += (_, _) => dialog.DialogResult = true;
			dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
			return dialog.ShowDialog() == true ? input.Text : null;
		}
	}
}