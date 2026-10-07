using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;

namespace Software.Archiver
{
	public partial class MainWindow : Window
	{
		private readonly ObservableCollection<string> _inputs = [];
		private readonly ArchiveLaunchRequest _startupRequest;
		private string? _currentArchive;
		private CancellationTokenSource? _operationCancellation;
		private bool _startupHandled;

		public MainWindow(string[]? arguments = null)
		{
			_startupRequest = ArchiveLaunchRequest.Parse(arguments ?? []);
			InitializeComponent();
			InputList.ItemsSource = _inputs;
		}

		private async void Window_Loaded(object sender, RoutedEventArgs eventArgs)
		{
			if (_startupHandled)
			{
				return;
			}

			_startupHandled = true;
			if (_startupRequest.Action == "None")
			{
				return;
			}

			if (_startupRequest.Action == "Add")
			{
				Workspace.SelectedIndex = 1;
				AddInputs(_startupRequest.Paths);
			}
			else if (_startupRequest.Action == "Extract")
			{
				if (await OpenArchiveAsync(_startupRequest.Paths[0]))
				{
					await ExtractAsync(selectedOnly: false);
				}
			}
			else if (_startupRequest.Action == "Open")
			{
				await OpenArchiveAsync(_startupRequest.Paths[0]);
			}
		}

		private async void Open_Click(object sender, RoutedEventArgs eventArgs)
		{
			var dialog = new OpenFileDialog { Title = "Open archive", Filter = "Archives|*.zip;*.7z;*.rar;*.tar|All files|*.*" };
			if (dialog.ShowDialog(this) == true)
			{
				await OpenArchiveAsync(dialog.FileName);
			}
		}

		private async Task<bool> OpenArchiveAsync(string path)
		{
			string password = ArchivePassword.Password;
			ArchiveItem[]? entries = null;
			bool success = await RunOperationAsync("Opening archive", (token, _) => entries = ArchiveService.ReadArchive(path, password, token));
			if (!success)
			{
				return false;
			}

			_currentArchive = Path.GetFullPath(path);
			ArchivePath.Text = _currentArchive;
			ArchivePath.ToolTip = _currentArchive;
			Title = $"{Path.GetFileName(_currentArchive)} - Archiver";
			EntryList.ItemsSource = entries;
			ApplyFilter();
			Workspace.SelectedIndex = 0;
			ExtractAllButton.IsEnabled = entries!.Length > 0;
			StatusText.Text = $"{entries.Length:N0} entries | {entries.Sum(entry => (decimal)entry.Size):N0} bytes unpacked";
			return true;
		}

		private void New_Click(object sender, RoutedEventArgs eventArgs)
		{
			_inputs.Clear();
			OutputPath.Text = "";
			Workspace.SelectedIndex = 1;
		}

		private void AddFiles_Click(object sender, RoutedEventArgs eventArgs)
		{
			var dialog = new OpenFileDialog { Title = "Add files", Multiselect = true, Filter = "All files|*.*" };
			if (dialog.ShowDialog(this) == true)
			{
				AddInputs(dialog.FileNames);
			}
		}

		private void AddFolder_Click(object sender, RoutedEventArgs eventArgs)
		{
			var dialog = new OpenFolderDialog { Title = "Add folders", Multiselect = true };
			if (dialog.ShowDialog(this) == true)
			{
				AddInputs(dialog.FolderNames);
			}
		}

		private void AddInputs(IEnumerable<string> paths)
		{
			try
			{
				foreach (string path in paths)
				{
					string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
					if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
					{
						throw new FileNotFoundException($"Input not found: {fullPath}");
					}

					if (!_inputs.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
					{
						_inputs.Add(fullPath);
					}
				}
				if (_inputs.Count > 0 && string.IsNullOrWhiteSpace(OutputPath.Text))
				{
					string first = _inputs[0];
					string name = Directory.Exists(first) ? Path.GetFileName(first) : Path.GetFileNameWithoutExtension(first);
					OutputPath.Text = Path.Combine(Path.GetDirectoryName(first) ?? first, (name.Length == 0 ? "Archive" : name) + ".zip");
				}
				StatusText.Text = $"{_inputs.Count} input(s)";
			}
			catch (Exception exception)
			{
				MessageBox.Show(this, exception.Message, "Cannot add input", MessageBoxButton.OK, MessageBoxImage.Error);
			}
		}

		private void Remove_Click(object sender, RoutedEventArgs eventArgs)
		{
			foreach (string path in InputList.SelectedItems.Cast<string>().ToArray())
			{
				_inputs.Remove(path);
			}

			StatusText.Text = $"{_inputs.Count} input(s)";
		}

		private void OutputBrowse_Click(object sender, RoutedEventArgs eventArgs)
		{
			var dialog = new SaveFileDialog { Title = "Create ZIP archive", Filter = "ZIP archive|*.zip", DefaultExt = ".zip", AddExtension = true, OverwritePrompt = false };
			if (dialog.ShowDialog(this) == true)
			{
				OutputPath.Text = dialog.FileName;
			}
		}

		private async void Create_Click(object sender, RoutedEventArgs eventArgs)
		{
			string output = OutputPath.Text.Trim();
			string[] sources = _inputs.ToArray();
			var compression = Enum.Parse<CompressionLevel>((string)((ComboBoxItem)Compression.SelectedItem).Tag);
			if (await RunOperationAsync("Creating ZIP", (token, progress) => ArchiveService.CreateZip(sources, output, compression, token, progress)))
			{
				await OpenArchiveAsync(output);
			}
		}

		private async void ExtractAll_Click(object sender, RoutedEventArgs eventArgs) => await ExtractAsync(selectedOnly: false);

		private async void ExtractSelected_Click(object sender, RoutedEventArgs eventArgs) => await ExtractAsync(selectedOnly: true);

		private async Task ExtractAsync(bool selectedOnly)
		{
			if (_currentArchive == null || _operationCancellation != null)
			{
				return;
			}

			string[]? selected = selectedOnly ? EntryList.SelectedItems.Cast<ArchiveItem>().Select(entry => entry.Name).ToArray() : null;
			if (selected is { Length: 0 })
			{
				return;
			}

			var dialog = new OpenFolderDialog { Title = "Choose extraction parent folder", InitialDirectory = Path.GetDirectoryName(_currentArchive) };
			if (dialog.ShowDialog(this) != true)
			{
				return;
			}

			string name = Path.GetFileNameWithoutExtension(_currentArchive);
			string destination = Path.Combine(dialog.FolderName, name.Length == 0 ? "Extracted" : name);
			if (MessageBox.Show(this, $"Extract to:\n{destination}\n\nExisting files will not be overwritten.", "Extract archive",
				MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
			{
				return;
			}

			string password = ArchivePassword.Password;
			string archive = _currentArchive;
			if (await RunOperationAsync("Extracting archive", (token, progress) => ArchiveService.Extract(archive, destination, selected, password, token, progress)))
			{
				StatusText.Text = $"Extracted to {destination}";
			}
		}

		private async Task<bool> RunOperationAsync(string name, Action<CancellationToken, IProgress<ArchiveProgress>> action)
		{
			if (_operationCancellation != null)
			{
				return false;
			}

			using var cancellation = new CancellationTokenSource();
			_operationCancellation = cancellation;
			Toolbar.IsEnabled = Workspace.IsEnabled = ArchiveHeader.IsEnabled = false;
			CancelButton.IsEnabled = true;
			OperationProgress.Value = 0;
			OperationProgress.IsIndeterminate = true;
			StatusText.Text = name + "...";
			var progress = new Progress<ArchiveProgress>(update =>
			{
				if (_operationCancellation != cancellation)
				{
					return;
				}

				OperationProgress.IsIndeterminate = false;
				OperationProgress.Value = update.Percent;
				StatusText.Text = update.Message;
			});
			try
			{
				await Task.Run(() => action(cancellation.Token, progress));
				StatusText.Text = name + " completed.";
				OperationProgress.Value = 100;
				return true;
			}
			catch (OperationCanceledException)
			{
				StatusText.Text = "Canceled. Files already extracted are kept.";
				return false;
			}
			catch (Exception exception)
			{
				StatusText.Text = name + " failed. Files already extracted are kept.";
				MessageBox.Show(this, exception.Message, name + " failed", MessageBoxButton.OK, MessageBoxImage.Error);
				return false;
			}
			finally
			{
				_operationCancellation = null;
				Toolbar.IsEnabled = Workspace.IsEnabled = ArchiveHeader.IsEnabled = true;
				CancelButton.IsEnabled = false;
				OperationProgress.IsIndeterminate = false;
			}
		}

		private void Cancel_Click(object sender, RoutedEventArgs eventArgs) => _operationCancellation?.Cancel();

		private void Window_Closing(object? sender, CancelEventArgs eventArgs)
		{
			if (_operationCancellation != null)
			{
				eventArgs.Cancel = true;
				_operationCancellation.Cancel();
				StatusText.Text = "Canceling the current operation. Close the window once it finishes.";
			}
		}

		private void EntrySelection_Changed(object sender, SelectionChangedEventArgs eventArgs)
			=> ExtractSelectedButton.IsEnabled = _currentArchive != null && EntryList.SelectedItems.Count > 0;

		private void Filter_Changed(object sender, TextChangedEventArgs eventArgs) => ApplyFilter();

		private void Window_DragOver(object sender, DragEventArgs eventArgs)
		{
			eventArgs.Effects = _operationCancellation == null && eventArgs.Data.GetDataPresent(DataFormats.FileDrop)
				? DragDropEffects.Copy : DragDropEffects.None;
			eventArgs.Handled = true;
		}

		private async void Window_Drop(object sender, DragEventArgs eventArgs)
		{
			if (_operationCancellation != null || eventArgs.Data.GetData(DataFormats.FileDrop) is not string[] paths)
			{
				return;
			}

			if (Workspace.SelectedIndex == 0 && paths.Length == 1 && File.Exists(paths[0])
				&& new[] { ".zip", ".7z", ".rar", ".tar" }.Contains(Path.GetExtension(paths[0]), StringComparer.OrdinalIgnoreCase))
			{
				await OpenArchiveAsync(paths[0]);
			}
			else
			{
				Workspace.SelectedIndex = 1;
				AddInputs(paths);
			}
		}

		private void ApplyFilter()
		{
			if (EntryList?.ItemsSource != null)
			{
				string filter = FilterBox.Text;
				CollectionViewSource.GetDefaultView(EntryList.ItemsSource).Filter = item => ((ArchiveItem)item).Name.Contains(filter, StringComparison.OrdinalIgnoreCase);
			}
		}

		private void Window_PreviewKeyDown(object sender, KeyEventArgs eventArgs)
		{
			if (_operationCancellation != null)
			{
				return;
			}

			if (Keyboard.Modifiers == ModifierKeys.Control && eventArgs.Key == Key.O)
			{
				eventArgs.Handled = true;
				Open_Click(this, new RoutedEventArgs());
			}
			else if (Keyboard.Modifiers == ModifierKeys.Control && eventArgs.Key == Key.N)
			{
				eventArgs.Handled = true;
				New_Click(this, new RoutedEventArgs());
			}
			else if (eventArgs.Key == Key.Delete && InputList.IsKeyboardFocusWithin)
			{
				eventArgs.Handled = true;
				Remove_Click(this, new RoutedEventArgs());
			}
		}
	}
}