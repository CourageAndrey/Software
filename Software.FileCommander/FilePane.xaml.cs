using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace Software.FileCommander
{
	public partial class FilePane : UserControl
	{
		private int _navigationVersion;
		private bool _updatingDrive;
		private string _sortProperty = nameof(FileEntry.Name);
		private ListSortDirection _sortDirection = ListSortDirection.Ascending;
		private CancellationTokenSource? _iconLoading;

		public string CurrentPath { get; private set; } = "";
		public bool IsLoading { get; private set; }
		public bool IsFileListFocused => FileList.IsKeyboardFocusWithin;
		public string[] SelectedPaths => FileList.SelectedItems.Cast<FileEntry>().Select(entry => entry.FullPath).ToArray();
		public event Action<FilePane>? Activated;

		public bool IsActive
		{
			set => PaneBorder.BorderBrush = value ? new SolidColorBrush(Color.FromRgb(37, 107, 123)) : Brushes.LightGray;
		}

		public FilePane()
		{
			InitializeComponent();
			DriveSelector.ItemsSource = DriveInfo.GetDrives().Select(drive => drive.Name).ToArray();
			FileList.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(Header_Click));
		}

		private void Pane_Activated(object sender, RoutedEventArgs eventArgs) => Activated?.Invoke(this);

		public void FocusList() => FileList.Focus();

		public Task RefreshAsync() => string.IsNullOrEmpty(CurrentPath) ? Task.CompletedTask : NavigateAsync(CurrentPath);

		public async Task NavigateAsync(string path)
		{
			int version = ++_navigationVersion;
			string[] selection = SelectedPaths;
			IsLoading = true;
			PaneStatus.Text = "Loading...";
			NavigationBar.IsEnabled = FileList.IsEnabled = false;
			try
			{
				string fullPath = Path.GetFullPath(path, CurrentPath.Length == 0 ? Environment.CurrentDirectory : CurrentPath);
				var entries = await Task.Run(() => FileOperations.ReadDirectory(fullPath));
				if (version != _navigationVersion)
				{
					return;
				}

				CurrentPath = fullPath;
				PathBox.Text = fullPath;
				FileList.ItemsSource = entries;
				_iconLoading?.Cancel();
				_iconLoading?.Dispose();
				_iconLoading = new CancellationTokenSource();
				ShellIcons.Load(entries, _iconLoading.Token);
				ApplySort();
				foreach (var entry in entries.Where(entry => selection.Contains(entry.FullPath, StringComparer.OrdinalIgnoreCase)))
				{
					FileList.SelectedItems.Add(entry);
				}

				_updatingDrive = true;
				DriveSelector.SelectedItem = Path.GetPathRoot(fullPath);
				_updatingDrive = false;
				UpdateStatus();
			}
			catch (Exception exception)
			{
				if (version == _navigationVersion)
				{
					PathBox.Text = CurrentPath;
					_updatingDrive = true;
					DriveSelector.SelectedItem = Path.GetPathRoot(CurrentPath);
					_updatingDrive = false;
					PaneStatus.Text = $"Cannot open folder: {exception.Message}";
				}
			}
			finally
			{
				if (version == _navigationVersion)
				{
					IsLoading = false;
					NavigationBar.IsEnabled = FileList.IsEnabled = true;
				}
			}
		}

		private async void Path_KeyDown(object sender, KeyEventArgs eventArgs)
		{
			if (eventArgs.Key == Key.Enter)
			{
				eventArgs.Handled = true;
				string input = Environment.ExpandEnvironmentVariables(PathBox.Text.Trim());
				await NavigateAsync(input);
				FocusList();
			}
		}

		private async void Drive_Changed(object sender, SelectionChangedEventArgs eventArgs)
		{
			if (!_updatingDrive && DriveSelector.SelectedItem is string root)
			{
				await NavigateAsync(root);
			}
		}

		private async void Up_Click(object sender, RoutedEventArgs eventArgs) => await GoUpAsync();

		private async Task GoUpAsync()
		{
			if (CurrentPath.Length > 0 && Directory.GetParent(CurrentPath) is DirectoryInfo parent)
			{
				await NavigateAsync(parent.FullName);
			}
		}

		private async void Refresh_Click(object sender, RoutedEventArgs eventArgs) => await RefreshAsync();

		private async void File_DoubleClick(object sender, MouseButtonEventArgs eventArgs)
		{
			if (ItemsControl.ContainerFromElement(FileList, eventArgs.OriginalSource as DependencyObject) is ListViewItem)
			{
				await OpenSelectedAsync();
			}
		}

		private async void File_KeyDown(object sender, KeyEventArgs eventArgs)
		{
			if (eventArgs.Key == Key.Enter)
			{
				eventArgs.Handled = true;
				await OpenSelectedAsync();
			}
			else if (eventArgs.Key == Key.Back)
			{
				eventArgs.Handled = true;
				await GoUpAsync();
			}
		}

		private async Task OpenSelectedAsync()
		{
			if (FileList.SelectedItem is not FileEntry entry)
			{
				return;
			}

			if (entry.IsDirectory)
			{
				await NavigateAsync(entry.FullPath);
				FocusList();
			}
			else
			{
				try
				{
					Process.Start(new ProcessStartInfo(entry.FullPath) { UseShellExecute = true });
				}
				catch (Exception exception)
				{
					PaneStatus.Text = $"Cannot open file: {exception.Message}";
				}
			}
		}

		private void Header_Click(object sender, RoutedEventArgs eventArgs)
		{
			if (eventArgs.OriginalSource is not GridViewColumnHeader { Content: string heading })
			{
				return;
			}

			string property = heading switch
			{
				"Name" => nameof(FileEntry.Name),
				"Size" => nameof(FileEntry.Size),
				"Modified" => nameof(FileEntry.Modified),
				_ => _sortProperty
			};
			_sortDirection = property == _sortProperty && _sortDirection == ListSortDirection.Ascending
				? ListSortDirection.Descending : ListSortDirection.Ascending;
			_sortProperty = property;
			ApplySort();
		}

		private void ApplySort()
		{
			var view = CollectionViewSource.GetDefaultView(FileList.ItemsSource);
			using (view.DeferRefresh())
			{
				view.SortDescriptions.Clear();
				view.SortDescriptions.Add(new SortDescription(nameof(FileEntry.IsDirectory), ListSortDirection.Descending));
				view.SortDescriptions.Add(new SortDescription(_sortProperty, _sortDirection));
			}
		}

		private void Selection_Changed(object sender, SelectionChangedEventArgs eventArgs) => UpdateStatus();

		private void UpdateStatus() => PaneStatus.Text = $"{FileList.Items.Count} items | {FileList.SelectedItems.Count} selected";
	}
}