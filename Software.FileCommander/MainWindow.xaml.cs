using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Software.FileCommander
{
	public partial class MainWindow : Window
	{
		private FilePane activePane;
		private bool busy;

		public MainWindow()
		{
			InitializeComponent();
			activePane = LeftPane;
			LeftPane.Activated += ActivatePane;
			RightPane.Activated += ActivatePane;
			ActivatePane(LeftPane);
		}

		private async void Window_Loaded(object sender, RoutedEventArgs eventArgs)
		{
			string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			await Task.WhenAll(LeftPane.NavigateAsync(home), RightPane.NavigateAsync(home));
		}

		private void ActivatePane(FilePane pane)
		{
			activePane = pane;
			LeftPane.IsActive = pane == LeftPane;
			RightPane.IsActive = pane == RightPane;
		}

		private async void Refresh_Click(object sender, RoutedEventArgs eventArgs)
		{
			if (!busy)
				await RefreshBothAsync();
		}

		private Task RefreshBothAsync() => Task.WhenAll(LeftPane.RefreshAsync(), RightPane.RefreshAsync());

		private void Window_Closing(object? sender, CancelEventArgs eventArgs)
		{
			if (busy)
			{
				eventArgs.Cancel = true;
				OperationStatus.Text = "Wait for the current file operation to finish before closing.";
			}
		}

		private async void Command_Click(object sender, RoutedEventArgs eventArgs)
		{
			if (sender is Button { Tag: string command })
				await ExecuteAsync(command);
		}

		private async void Window_PreviewKeyDown(object sender, KeyEventArgs eventArgs)
		{
			if (busy || Keyboard.Modifiers != ModifierKeys.None || Keyboard.FocusedElement is TextBox)
				return;

			string? command = eventArgs.Key switch
			{
				Key.F2 => "Rename",
				Key.F5 => "Copy",
				Key.F6 => "Move",
				Key.F7 => "NewFolder",
				Key.F8 or Key.Delete => "Delete",
				_ => null
			};
			if (command != null)
			{
				eventArgs.Handled = true;
				await ExecuteAsync(command);
			}
			else if (eventArgs.Key == Key.Tab && activePane.IsFileListFocused)
			{
				eventArgs.Handled = true;
				var other = activePane == LeftPane ? RightPane : LeftPane;
				ActivatePane(other);
				other.FocusList();
			}
		}

		private async Task ExecuteAsync(string command)
		{
			if (busy || activePane.IsLoading || string.IsNullOrEmpty(activePane.CurrentPath))
				return;

			var sourcePane = activePane;
			var destinationPane = sourcePane == LeftPane ? RightPane : LeftPane;
			string sourceFolder = sourcePane.CurrentPath;
			string targetFolder = destinationPane.CurrentPath;
			string[] selected = sourcePane.SelectedPaths;
			if (command != "NewFolder" && selected.Length == 0)
			{
				OperationStatus.Text = "Select a file or folder first.";
				return;
			}

			Action operation;
			string success;
			switch (command)
			{
				case "Copy":
				case "Move":
					if (destinationPane.IsLoading || string.IsNullOrEmpty(targetFolder))
						return;
					if (MessageBox.Show(this, $"{command} {selected.Length} selected item(s) to:\n{targetFolder}?", command,
						MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
						return;
					operation = command == "Copy" ? () => FileOperations.Copy(selected, targetFolder) : () => FileOperations.Move(selected, targetFolder);
					success = $"{command} completed: {selected.Length} item(s).";
					break;
				case "Rename":
					if (selected.Length != 1)
					{
						OperationStatus.Text = "Select exactly one item to rename.";
						return;
					}
					string? name = PromptForName("Rename", Path.GetFileName(selected[0]));
					if (name == null)
						return;
					operation = () => FileOperations.Rename(selected[0], name);
					success = "Item renamed.";
					break;
				case "NewFolder":
					string? folderName = PromptForName("New folder", "New folder");
					if (folderName == null)
						return;
					operation = () => FileOperations.CreateFolder(sourceFolder, folderName);
					success = "Folder created.";
					break;
				case "Delete":
					if (MessageBox.Show(this, $"Send {selected.Length} selected item(s) to the Recycle Bin?", "Delete",
						MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
						return;
					operation = () => FileOperations.Recycle(selected);
					success = "Selected items sent to the Recycle Bin.";
					break;
				default:
					return;
			}

			busy = true;
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
				await RefreshBothAsync();
				busy = false;
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