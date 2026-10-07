using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Software.FileCommander;

namespace Software.UnitTests
{
	public class FileOperationTests
	{
		private string _root = null!;
		private string _source = null!;
		private string _destination = null!;

		[SetUp]
		public void Setup()
		{
			_root = Path.Combine(Path.GetTempPath(), $"FileCommanderTests-{Guid.NewGuid():N}");
			_source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
			_destination = Directory.CreateDirectory(Path.Combine(_root, "destination")).FullName;
		}

		[TearDown]
		public void TearDown() => Directory.Delete(_root, recursive: true);

		[Test]
		public void DirectoryListingIncludesFoldersFirstAndFileMetadata()
		{
			File.WriteAllText(Path.Combine(_source, "a.txt"), "abc");
			Directory.CreateDirectory(Path.Combine(_source, "z-folder"));
			var entries = FileOperations.ReadDirectory(_source);

			Assert.That(entries, Has.Length.EqualTo(2));
			Assert.That(entries[0].Name, Is.EqualTo("z-folder"));
			Assert.That(entries[0].IsDirectory, Is.True);
			Assert.That(entries[1].Size, Is.EqualTo(3));
		}

		[TestCase(false)]
		[TestCase(true)]
		public void TransfersFilesAndNestedFolders(bool move)
		{
			string file = Path.Combine(_source, "file.txt");
			File.WriteAllText(file, "file content");
			string folder = Directory.CreateDirectory(Path.Combine(_source, "folder")).FullName;
			Directory.CreateDirectory(Path.Combine(folder, "nested"));
			File.WriteAllText(Path.Combine(folder, "nested", "child.txt"), "nested content");

			if (move)
			{
				FileOperations.Move([file, folder], _destination);
			}
			else
			{
				FileOperations.Copy([file, folder], _destination);
			}

			Assert.That(File.ReadAllText(Path.Combine(_destination, "file.txt")), Is.EqualTo("file content"));
			Assert.That(File.ReadAllText(Path.Combine(_destination, "folder", "nested", "child.txt")), Is.EqualTo("nested content"));
			Assert.That(File.Exists(file), Is.EqualTo(!move));
			Assert.That(Directory.Exists(folder), Is.EqualTo(!move));
		}

		[TestCase(false)]
		[TestCase(true)]
		public void DestinationConflictRejectsTheWholeBatchBeforeChangingFiles(bool move)
		{
			string first = Path.Combine(_source, "first.txt");
			string second = Path.Combine(_source, "second.txt");
			File.WriteAllText(first, "first");
			File.WriteAllText(second, "source content");
			File.WriteAllText(Path.Combine(_destination, "second.txt"), "existing content");

			Assert.Throws<IOException>(() =>
			{
				if (move)
				{
					FileOperations.Move([first, second], _destination);
				}
				else
				{
					FileOperations.Copy([first, second], _destination);
				}
			});
			Assert.That(File.Exists(Path.Combine(_destination, "first.txt")), Is.False);
			Assert.That(File.Exists(first), Is.True);
			Assert.That(File.ReadAllText(Path.Combine(_destination, "second.txt")), Is.EqualTo("existing content"));
		}

		[Test]
		public void RejectsCopyingFoldersIntoTheirOwnDescendants()
		{
			string child = Directory.CreateDirectory(Path.Combine(_source, "child")).FullName;
			Assert.Throws<IOException>(() => FileOperations.Copy([_source], child));
			Assert.That(Directory.Exists(Path.Combine(child, "source")), Is.False);
		}

		[Test]
		public void RejectsTransferringAnItemToTheSameFolder()
		{
			string file = Path.Combine(_source, "file.txt");
			File.WriteAllText(file, "unchanged");
			Assert.Throws<IOException>(() => FileOperations.Move([file], _source));
			Assert.That(File.ReadAllText(file), Is.EqualTo("unchanged"));
		}

		[Test]
		public void CreatesFoldersAndRenamesFilesAndDirectories()
		{
			FileOperations.CreateFolder(_source, "new-folder");
			FileOperations.Rename(Path.Combine(_source, "new-folder"), "renamed-folder");
			Assert.That(Directory.Exists(Path.Combine(_source, "renamed-folder")), Is.True);
			string file = Path.Combine(_source, "old.txt");
			File.WriteAllText(file, "preserved");
			FileOperations.Rename(file, "new.txt");
			Assert.That(File.Exists(file), Is.False);
			Assert.That(File.ReadAllText(Path.Combine(_source, "new.txt")), Is.EqualTo("preserved"));
		}

		[TestCase("..")]
		[TestCase("../escape")]
		[TestCase("bad:name")]
		[TestCase("trailing.")]
		public void RejectsInvalidNames(string name)
		{
			Assert.Throws<ArgumentException>(() => FileOperations.CreateFolder(_source, name));
			Assert.That(Directory.GetFileSystemEntries(_source), Is.Empty);
		}

		[Test]
		public void RenameAndNewFolderNeverOverwriteExistingItems()
		{
			string original = Path.Combine(_source, "original.txt");
			File.WriteAllText(original, "original");
			File.WriteAllText(Path.Combine(_source, "existing.txt"), "existing");
			Assert.Throws<IOException>(() => FileOperations.Rename(original, "existing.txt"));
			FileOperations.CreateFolder(_source, "folder");
			Assert.Throws<IOException>(() => FileOperations.CreateFolder(_source, "folder"));
			Assert.That(File.ReadAllText(original), Is.EqualTo("original"));
		}
	}

	[Apartment(ApartmentState.STA)]
	public class FileCommanderUiTests
	{
		private MainWindow _window = null!;
		private string _root = null!;
		private FilePane Left => (FilePane)_window.FindName("LeftPane");
		private FilePane Right => (FilePane)_window.FindName("RightPane");

		[SetUp]
		public void Setup()
		{
			_root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"FileCommanderUiTests-{Guid.NewGuid():N}")).FullName;
			Directory.CreateDirectory(Path.Combine(_root, "child"));
			File.WriteAllText(Path.Combine(_root, "file.txt"), "content");
			_window = new MainWindow();
		}

		[TearDown]
		public void TearDown()
		{
			_window.Close();
			Directory.Delete(_root, recursive: true);
		}

		[Test]
		public void PanesNavigateIndependentlyAndDisplayDirectoryContents()
		{
			RunOnDispatcher(async () =>
			{
				_window.Show();
				await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
				await Task.WhenAll(Left.NavigateAsync(_root), Right.NavigateAsync(Path.Combine(_root, "child")));
				Assert.That(Left.CurrentPath, Is.EqualTo(_root));
				Assert.That(Right.CurrentPath, Is.EqualTo(Path.Combine(_root, "child")));
				Assert.That(((ListView)Left.FindName("FileList")).Items.Count, Is.EqualTo(2));
				Assert.That(((ListView)Right.FindName("FileList")).Items.Count, Is.EqualTo(0));
				Assert.That(Left.ActualWidth, Is.GreaterThan(350));
				Assert.That(Right.ActualWidth, Is.GreaterThan(350));
			});
		}

		[Test]
		public void InvalidNavigationKeepsTheExistingDirectoryAndDisplaysAnError()
		{
			RunOnDispatcher(async () =>
			{
				await Left.NavigateAsync(_root);
				await Left.NavigateAsync(Path.Combine(_root, "missing"));
				Assert.That(Left.CurrentPath, Is.EqualTo(_root));
				Assert.That(((TextBlock)Left.FindName("PaneStatus")).Text, Does.StartWith("Cannot open folder:"));
				Assert.That(Left.IsLoading, Is.False);
			});
		}

		[Test]
		public void RefreshPreservesSelectionAndRelativeNavigationUsesTheCurrentFolder()
		{
			RunOnDispatcher(async () =>
			{
				await Left.NavigateAsync(_root);
				var list = (ListView)Left.FindName("FileList");
				list.SelectedItem = list.Items.Cast<FileEntry>().Single(entry => entry.Name == "file.txt");
				await Left.RefreshAsync();
				Assert.That(Left.SelectedPaths, Is.EqualTo(new[] { Path.Combine(_root, "file.txt") }));
				await Left.NavigateAsync("child");
				Assert.That(Left.CurrentPath, Is.EqualTo(Path.Combine(_root, "child")));
			});
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
			Assert.That(task.IsCompleted, Is.True, "Folder navigation did not complete within 15 seconds.");
			task.GetAwaiter().GetResult();
		}
	}
}