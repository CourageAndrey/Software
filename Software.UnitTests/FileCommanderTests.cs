using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Software.FileCommander;

namespace Software.UnitTests
{
    public class FileOperationTests
    {
        private string root = null!;
        private string source = null!;
        private string destination = null!;

        [SetUp]
        public void Setup()
        {
            root = Path.Combine(Path.GetTempPath(), $"FileCommanderTests-{Guid.NewGuid():N}");
            source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
            destination = Directory.CreateDirectory(Path.Combine(root, "destination")).FullName;
        }

        [TearDown]
        public void TearDown() => Directory.Delete(root, recursive: true);

        [Test]
        public void DirectoryListingIncludesFoldersFirstAndFileMetadata()
        {
            File.WriteAllText(Path.Combine(source, "a.txt"), "abc");
            Directory.CreateDirectory(Path.Combine(source, "z-folder"));
            var entries = FileOperations.ReadDirectory(source);

            Assert.That(entries, Has.Length.EqualTo(2));
            Assert.That(entries[0].Name, Is.EqualTo("z-folder"));
            Assert.That(entries[0].IsDirectory, Is.True);
            Assert.That(entries[1].Size, Is.EqualTo(3));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TransfersFilesAndNestedFolders(bool move)
        {
            string file = Path.Combine(source, "file.txt");
            File.WriteAllText(file, "file content");
            string folder = Directory.CreateDirectory(Path.Combine(source, "folder")).FullName;
            Directory.CreateDirectory(Path.Combine(folder, "nested"));
            File.WriteAllText(Path.Combine(folder, "nested", "child.txt"), "nested content");

            if (move)
                FileOperations.Move([file, folder], destination);
            else
                FileOperations.Copy([file, folder], destination);

            Assert.That(File.ReadAllText(Path.Combine(destination, "file.txt")), Is.EqualTo("file content"));
            Assert.That(File.ReadAllText(Path.Combine(destination, "folder", "nested", "child.txt")), Is.EqualTo("nested content"));
            Assert.That(File.Exists(file), Is.EqualTo(!move));
            Assert.That(Directory.Exists(folder), Is.EqualTo(!move));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DestinationConflictRejectsTheWholeBatchBeforeChangingFiles(bool move)
        {
            string first = Path.Combine(source, "first.txt");
            string second = Path.Combine(source, "second.txt");
            File.WriteAllText(first, "first");
            File.WriteAllText(second, "source content");
            File.WriteAllText(Path.Combine(destination, "second.txt"), "existing content");

            Assert.Throws<IOException>(() =>
            {
                if (move)
                    FileOperations.Move([first, second], destination);
                else
                    FileOperations.Copy([first, second], destination);
            });
            Assert.That(File.Exists(Path.Combine(destination, "first.txt")), Is.False);
            Assert.That(File.Exists(first), Is.True);
            Assert.That(File.ReadAllText(Path.Combine(destination, "second.txt")), Is.EqualTo("existing content"));
        }

        [Test]
        public void RejectsCopyingFoldersIntoTheirOwnDescendants()
        {
            string child = Directory.CreateDirectory(Path.Combine(source, "child")).FullName;
            Assert.Throws<IOException>(() => FileOperations.Copy([source], child));
            Assert.That(Directory.Exists(Path.Combine(child, "source")), Is.False);
        }

        [Test]
        public void RejectsTransferringAnItemToTheSameFolder()
        {
            string file = Path.Combine(source, "file.txt");
            File.WriteAllText(file, "unchanged");
            Assert.Throws<IOException>(() => FileOperations.Move([file], source));
            Assert.That(File.ReadAllText(file), Is.EqualTo("unchanged"));
        }

        [Test]
        public void CreatesFoldersAndRenamesFilesAndDirectories()
        {
            FileOperations.CreateFolder(source, "new-folder");
            FileOperations.Rename(Path.Combine(source, "new-folder"), "renamed-folder");
            Assert.That(Directory.Exists(Path.Combine(source, "renamed-folder")), Is.True);
            string file = Path.Combine(source, "old.txt");
            File.WriteAllText(file, "preserved");
            FileOperations.Rename(file, "new.txt");
            Assert.That(File.Exists(file), Is.False);
            Assert.That(File.ReadAllText(Path.Combine(source, "new.txt")), Is.EqualTo("preserved"));
        }

        [TestCase("..")]
        [TestCase("../escape")]
        [TestCase("bad:name")]
        [TestCase("trailing.")]
        public void RejectsInvalidNames(string name)
        {
            Assert.Throws<ArgumentException>(() => FileOperations.CreateFolder(source, name));
            Assert.That(Directory.GetFileSystemEntries(source), Is.Empty);
        }

        [Test]
        public void RenameAndNewFolderNeverOverwriteExistingItems()
        {
            string original = Path.Combine(source, "original.txt");
            File.WriteAllText(original, "original");
            File.WriteAllText(Path.Combine(source, "existing.txt"), "existing");
            Assert.Throws<IOException>(() => FileOperations.Rename(original, "existing.txt"));
            FileOperations.CreateFolder(source, "folder");
            Assert.Throws<IOException>(() => FileOperations.CreateFolder(source, "folder"));
            Assert.That(File.ReadAllText(original), Is.EqualTo("original"));
        }
    }

    [Apartment(ApartmentState.STA)]
    public class FileCommanderUiTests
    {
        private MainWindow window = null!;
        private string root = null!;
        private FilePane Left => (FilePane)window.FindName("LeftPane");
        private FilePane Right => (FilePane)window.FindName("RightPane");

        [SetUp]
        public void Setup()
        {
            root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"FileCommanderUiTests-{Guid.NewGuid():N}")).FullName;
            Directory.CreateDirectory(Path.Combine(root, "child"));
            File.WriteAllText(Path.Combine(root, "file.txt"), "content");
            window = new MainWindow();
        }

        [TearDown]
        public void TearDown()
        {
            window.Close();
            Directory.Delete(root, recursive: true);
        }

        [Test]
        public void PanesNavigateIndependentlyAndDisplayDirectoryContents()
        {
            RunOnDispatcher(async () =>
            {
                window.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                await Task.WhenAll(Left.NavigateAsync(root), Right.NavigateAsync(Path.Combine(root, "child")));
                Assert.That(Left.CurrentPath, Is.EqualTo(root));
                Assert.That(Right.CurrentPath, Is.EqualTo(Path.Combine(root, "child")));
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
                await Left.NavigateAsync(root);
                await Left.NavigateAsync(Path.Combine(root, "missing"));
                Assert.That(Left.CurrentPath, Is.EqualTo(root));
                Assert.That(((TextBlock)Left.FindName("PaneStatus")).Text, Does.StartWith("Cannot open folder:"));
                Assert.That(Left.IsLoading, Is.False);
            });
        }

        [Test]
        public void RefreshPreservesSelectionAndRelativeNavigationUsesTheCurrentFolder()
        {
            RunOnDispatcher(async () =>
            {
                await Left.NavigateAsync(root);
                var list = (ListView)Left.FindName("FileList");
                list.SelectedItem = list.Items.Cast<FileEntry>().Single(entry => entry.Name == "file.txt");
                await Left.RefreshAsync();
                Assert.That(Left.SelectedPaths, Is.EqualTo(new[] { Path.Combine(root, "file.txt") }));
                await Left.NavigateAsync("child");
                Assert.That(Left.CurrentPath, Is.EqualTo(Path.Combine(root, "child")));
            });
        }

        private void RunOnDispatcher(Func<Task> action)
        {
            Task task = window.Dispatcher.InvokeAsync(action).Task.Unwrap();
            var frame = new DispatcherFrame();
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            timeout.Tick += (_, _) => frame.Continue = false;
            _ = task.ContinueWith(_ => window.Dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            timeout.Start();
            Dispatcher.PushFrame(frame);
            timeout.Stop();
            Assert.That(task.IsCompleted, Is.True, "Folder navigation did not complete within 15 seconds.");
            task.GetAwaiter().GetResult();
        }
    }
}