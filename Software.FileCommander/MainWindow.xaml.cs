using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Software.FileCommander
{
	public partial class MainWindow : Window
	{
		private FilePane _activePane;
		private bool _busy;

		public MainWindow()
		{
			InitializeComponent();
			_activePane = LeftPane;
			LeftPane.Activated += ActivatePane;
			RightPane.Activated += ActivatePane;
			LeftPane.CommandRequested += Pane_CommandRequested;
			RightPane.CommandRequested += Pane_CommandRequested;
			ActivatePane(LeftPane);
		}

		private async void Pane_CommandRequested(FilePane pane, FileCommand command)
		{
			ActivatePane(pane);
			if (command == FileCommand.Refresh)
			{
				await Task.WhenAll(LeftPane.RefreshAsync(), RightPane.RefreshAsync());
			}
			else
			{
				await ExecuteAsync(command);
			}
		}

		private async void Window_Loaded(object sender, RoutedEventArgs eventArgs)
		{
			string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			await Task.WhenAll(LeftPane.NavigateAsync(home), RightPane.NavigateAsync(home));
		}

		private void ActivatePane(FilePane pane)
		{
			_activePane = pane;
			LeftPane.IsActive = pane == LeftPane;
			RightPane.IsActive = pane == RightPane;
		}

		private void Window_Closing(object? sender, CancelEventArgs eventArgs)
		{
			if (_busy)
			{
				eventArgs.Cancel = true;
				OperationStatus.Text = "Wait for the current file operation to finish before closing.";
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
			if (_busy || Keyboard.Modifiers != ModifierKeys.None || Keyboard.FocusedElement is TextBox)
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
			else if (eventArgs.Key == Key.Tab && _activePane.IsFileListFocused)
			{
				eventArgs.Handled = true;
				var other = _activePane == LeftPane ? RightPane : LeftPane;
				ActivatePane(other);
				other.FocusList();
			}
		}

		private async Task ExecuteAsync(FileCommand command)
		{
			if (_busy || _activePane.IsLoading || string.IsNullOrEmpty(_activePane.CurrentPath))
			{
				return;
			}

			var sourcePane = _activePane;
			var destinationPane = sourcePane == LeftPane ? RightPane : LeftPane;
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
				await Task.WhenAll(LeftPane.RefreshAsync(), RightPane.RefreshAsync());
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