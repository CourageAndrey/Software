using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Software.Archiver;

namespace Software.UnitTests
{
	public class ArchiveServiceTests
	{
		private string _root = null!;
		private string _archivePath = null!;
		private string _destination = null!;

		[SetUp]
		public void Setup()
		{
			_root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ArchiverTests-{Guid.NewGuid():N}")).FullName;
			_archivePath = Path.Combine(_root, "test.zip");
			_destination = Path.Combine(_root, "extracted");
		}

		[TearDown]
		public void TearDown() => Directory.Delete(_root, recursive: true);

		private void MakeZip(params (string Name, string Content)[] entries)
		{
			using var archive = ZipFile.Open(_archivePath, ZipArchiveMode.Create);
			foreach (var (name, content) in entries)
			{
				var entry = archive.CreateEntry(name);
				using var writer = new StreamWriter(entry.Open());
				writer.Write(content);
			}
		}

		[Test]
		public void ZipRoundTripPreservesNestedFilesAndEmptyFolders()
		{
			string source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
			Directory.CreateDirectory(Path.Combine(source, "empty"));
			Directory.CreateDirectory(Path.Combine(source, "nested"));
			File.WriteAllText(Path.Combine(source, "nested", "hello.txt"), "hello world");
			ArchiveService.CreateZip([source], _archivePath);
			var entries = ArchiveService.ReadArchive(_archivePath);
			Assert.That(entries.Select(entry => entry.Name), Does.Contain("source/nested/hello.txt"));
			ArchiveService.Extract(_archivePath, _destination);
			Assert.That(File.ReadAllText(Path.Combine(_destination, "source", "nested", "hello.txt")), Is.EqualTo("hello world"));
			Assert.That(Directory.Exists(Path.Combine(_destination, "source", "empty")), Is.True);
		}

		[Test]
		public void ExtractingSelectedFolderIncludesDescendantsOnly()
		{
			MakeZip(("folder/", ""), ("folder/child.txt", "child"), ("other.txt", "other"));
			ArchiveService.Extract(_archivePath, _destination, ["folder/"]);
			Assert.That(File.ReadAllText(Path.Combine(_destination, "folder", "child.txt")), Is.EqualTo("child"));
			Assert.That(File.Exists(Path.Combine(_destination, "other.txt")), Is.False);
		}

		[Test]
		public void ExtractingSelectedFileDoesNotExtractOtherEntries()
		{
			MakeZip(("first.txt", "first"), ("second.txt", "second"));
			ArchiveService.Extract(_archivePath, _destination, ["second.txt"]);
			Assert.That(File.Exists(Path.Combine(_destination, "first.txt")), Is.False);
			Assert.That(File.ReadAllText(Path.Combine(_destination, "second.txt")), Is.EqualTo("second"));
		}

		[TestCase("../outside.txt")]
		[TestCase("folder/../../outside.txt")]
		[TestCase("/rooted.txt")]
		[TestCase("C:/absolute.txt")]
		[TestCase("folder\\..\\outside.txt")]
		[TestCase("stream.txt:secret")]
		[TestCase("CON.txt")]
		[TestCase("folder/trailing.")]
		public void UnsafeArchivePathsAreRejectedBeforeCreatingAnyOutput(string name)
		{
			MakeZip(("safe.txt", "safe"), (name, "unsafe"));
			Assert.Throws<InvalidDataException>(() => ArchiveService.Extract(_archivePath, _destination));
			Assert.That(Directory.Exists(_destination), Is.False);
			Assert.That(File.Exists(Path.Combine(_root, "outside.txt")), Is.False);
		}

		[Test]
		public void ExtractionDoesNotOverwriteFilesOrPartiallyProcessKnownConflicts()
		{
			MakeZip(("first.txt", "first"), ("existing.txt", "new"));
			Directory.CreateDirectory(_destination);
			File.WriteAllText(Path.Combine(_destination, "existing.txt"), "original");
			Assert.Throws<IOException>(() => ArchiveService.Extract(_archivePath, _destination));
			Assert.That(File.ReadAllText(Path.Combine(_destination, "existing.txt")), Is.EqualTo("original"));
			Assert.That(File.Exists(Path.Combine(_destination, "first.txt")), Is.False);
		}

		[Test]
		public void CaseCollidingEntriesAndFileFolderConflictsAreRejected()
		{
			MakeZip(("FILE.txt", "upper"), ("file.txt", "lower"));
			Assert.Throws<InvalidDataException>(() => ArchiveService.Extract(_archivePath, _destination));
			File.Delete(_archivePath);
			MakeZip(("folder", "file"), ("folder/child.txt", "child"));
			Assert.Throws<InvalidDataException>(() => ArchiveService.Extract(_archivePath, _destination));
			Assert.That(Directory.Exists(_destination), Is.False);
		}

		[Test]
		public void ExtractionSizeLimitRejectsOversizedArchivesBeforeWriting()
		{
			MakeZip(("too-large.txt", "1234567890"));
			Assert.Throws<InvalidDataException>(() => ArchiveService.Extract(_archivePath, _destination, maximumBytes: 5));
			Assert.That(Directory.Exists(_destination), Is.False);
		}

		[Test]
		public void ArchiveCreationRefusesExistingOutputAndSelfInclusion()
		{
			string input = Path.Combine(_root, "input.txt");
			File.WriteAllText(input, "data");
			ArchiveService.CreateZip([input], _archivePath);
			byte[] original = File.ReadAllBytes(_archivePath);
			Assert.Throws<IOException>(() => ArchiveService.CreateZip([input], _archivePath));
			Assert.That(File.ReadAllBytes(_archivePath), Is.EqualTo(original));
			Assert.Throws<IOException>(() => ArchiveService.CreateZip([_root], Path.Combine(_root, "inside.zip")));
			Assert.That(File.Exists(Path.Combine(_root, "inside.zip")), Is.False);
		}

		[Test]
		public void DuplicateInputNamesAreRejectedWithoutCreatingAnArchive()
		{
			string left = Directory.CreateDirectory(Path.Combine(_root, "left")).FullName;
			string right = Directory.CreateDirectory(Path.Combine(_root, "right")).FullName;
			File.WriteAllText(Path.Combine(left, "same.txt"), "left");
			File.WriteAllText(Path.Combine(right, "same.txt"), "right");
			Assert.Throws<IOException>(() => ArchiveService.CreateZip([Path.Combine(left, "same.txt"), Path.Combine(right, "same.txt")], _archivePath));
			Assert.That(File.Exists(_archivePath), Is.False);
		}

		[Test]
		public void CancellationDuringCreationLeavesNoArchiveOrTemporaryFile()
		{
			string input = Path.Combine(_root, "input.txt");
			File.WriteAllText(input, "data");
			using var cancellation = new CancellationTokenSource();
			var progress = new CallbackProgress(_ => cancellation.Cancel());
			Assert.Throws<OperationCanceledException>(() => ArchiveService.CreateZip([input], _archivePath,
				cancellationToken: cancellation.Token, progress: progress));
			Assert.That(File.Exists(_archivePath), Is.False);
			Assert.That(Directory.GetFiles(_root, ".Software.Archiver-*.tmp"), Is.Empty);
		}

		[Test]
		public void TarArchiveIsListedAndExtractedWithRelativeEntryPaths()
		{
			string tarPath = Path.Combine(_root, "sample.tar");
			using (var output = File.Create(tarPath))
			using (var writer = new TarWriter(output))
			{
				writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "./"));
				using var content = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("tar content"));
				writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "./file.txt") { DataStream = content });
			}
			Assert.That(ArchiveService.ReadArchive(tarPath).Any(entry => entry.Name.EndsWith("file.txt")), Is.True);
			ArchiveService.Extract(tarPath, _destination);
			Assert.That(File.ReadAllText(Path.Combine(_destination, "file.txt")), Is.EqualTo("tar content"));
		}

		[Test]
		public void TarSymbolicLinksAreRejected()
		{
			string tarPath = Path.Combine(_root, "links.tar");
			using (var output = File.Create(tarPath))
			using (var writer = new TarWriter(output))
				writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "link") { LinkName = "../outside" });
			Assert.Throws<InvalidDataException>(() => ArchiveService.Extract(tarPath, _destination));
			Assert.That(Directory.Exists(_destination), Is.False);
		}

		[Test]
		public void MalformedArchiveFailsWithoutCreatingOutput()
		{
			File.WriteAllText(_archivePath, "not an archive");
			Assert.Catch(() => ArchiveService.Extract(_archivePath, _destination));
			Assert.That(Directory.Exists(_destination), Is.False);
		}

		private sealed class CallbackProgress(Action<ArchiveProgress> callback) : IProgress<ArchiveProgress>
		{
			public void Report(ArchiveProgress value) => callback(value);
		}
	}

	public class ArchiveLaunchTests
	{
		[Test]
		public void ParsesAssociationAndContextMenuArgumentsWithSpaces()
		{
			string path = Path.Combine(Path.GetTempPath(), "folder with spaces", "archive.zip");
			Assert.That(ArchiveLaunchRequest.Parse([path]).Action, Is.EqualTo("Open"));
			Assert.That(ArchiveLaunchRequest.Parse(["--open", path]).Paths, Is.EqualTo(new[] { path }));
			Assert.That(ArchiveLaunchRequest.Parse(["--extract", path]).Action, Is.EqualTo("Extract"));
			Assert.That(ArchiveLaunchRequest.Parse(["--add", path, path + ".txt"]).Paths, Has.Length.EqualTo(2));
			Assert.That(ArchiveLaunchRequest.Parse([]).Action, Is.EqualTo("None"));
		}

		[TestCase("--unknown")]
		[TestCase("--add")]
		[TestCase("--extract")]
		public void RejectsIncompleteOrUnknownCommands(string command)
			=> Assert.Throws<ArgumentException>(() => ArchiveLaunchRequest.Parse([command]));
	}

	[Apartment(ApartmentState.STA)]
	public class ArchiverUiTests
	{
		[Test]
		public void ArchiveViewListsEntriesAndFiltersThem()
		{
			string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ArchiverUiTests-{Guid.NewGuid():N}")).FullName;
			string archivePath = Path.Combine(root, "sample.zip");
			using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
			{
				zip.CreateEntry("first.txt");
				zip.CreateEntry("second.txt");
			}
			var window = new MainWindow(["--open", archivePath]);
			try
			{
				var frame = new DispatcherFrame();
				var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
				timeout.Tick += (_, _) => frame.Continue = false;
				var status = (TextBlock)window.FindName("StatusText");
				EventHandler? completion = null;
				completion = (_, _) =>
				{
					if (((DataGrid)window.FindName("EntryList")).Items.Count == 2)
						frame.Continue = false;
				};
				window.LayoutUpdated += completion;
				window.Show();
				timeout.Start();
				Dispatcher.PushFrame(frame);
				timeout.Stop();
				window.LayoutUpdated -= completion;
				var list = (DataGrid)window.FindName("EntryList");
				Assert.That(list.Items.Count, Is.EqualTo(2), status.Text);
				((TextBox)window.FindName("FilterBox")).Text = "second";
				Assert.That(list.Items.Count, Is.EqualTo(1));
				Assert.That(((ArchiveItem)list.Items[0]).Name, Is.EqualTo("second.txt"));
			}
			finally
			{
				window.Close();
				Directory.Delete(root, recursive: true);
			}
		}
	}
}