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
		private string root = null!;
		private string archivePath = null!;
		private string destination = null!;

		[SetUp]
		public void Setup()
		{
			root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ArchiverTests-{Guid.NewGuid():N}")).FullName;
			archivePath = Path.Combine(root, "test.zip");
			destination = Path.Combine(root, "extracted");
		}

		[TearDown]
		public void TearDown() => Directory.Delete(root, recursive: true);

		private void MakeZip(params (string Name, string Content)[] entries)
		{
			using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
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
			string source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
			Directory.CreateDirectory(Path.Combine(source, "empty"));
			Directory.CreateDirectory(Path.Combine(source, "nested"));
			File.WriteAllText(Path.Combine(source, "nested", "hello.txt"), "hello world");
			ArchiveService.CreateZip([source], archivePath);
			var entries = ArchiveService.ReadArchive(archivePath);
			Assert.That(entries.Select(entry => entry.Name), Does.Contain("source/nested/hello.txt"));
			ArchiveService.Extract(archivePath, destination);
			Assert.That(File.ReadAllText(Path.Combine(destination, "source", "nested", "hello.txt")), Is.EqualTo("hello world"));
			Assert.That(Directory.Exists(Path.Combine(destination, "source", "empty")), Is.True);
		}

		[Test]
		public void ExtractingSelectedFolderIncludesDescendantsOnly()
		{
			MakeZip(("folder/", ""), ("folder/child.txt", "child"), ("other.txt", "other"));
			ArchiveService.Extract(archivePath, destination, ["folder/"]);
			Assert.That(File.ReadAllText(Path.Combine(destination, "folder", "child.txt")), Is.EqualTo("child"));
			Assert.That(File.Exists(Path.Combine(destination, "other.txt")), Is.False);
		}

		[Test]
		public void ExtractingSelectedFileDoesNotExtractOtherEntries()
		{
			MakeZip(("first.txt", "first"), ("second.txt", "second"));
			ArchiveService.Extract(archivePath, destination, ["second.txt"]);
			Assert.That(File.Exists(Path.Combine(destination, "first.txt")), Is.False);
			Assert.That(File.ReadAllText(Path.Combine(destination, "second.txt")), Is.EqualTo("second"));
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
			Assert.Throws<InvalidDataException>(() => ArchiveService.Extract(archivePath, destination));
			Assert.That(Directory.Exists(destination), Is.False);
			Assert.That(File.Exists(Path.Combine(root, "outside.txt")), Is.False);
		}

		[Test]
		public void ExtractionDoesNotOverwriteFilesOrPartiallyProcessKnownConflicts()
		{
			MakeZip(("first.txt", "first"), ("existing.txt", "new"));
			Directory.CreateDirectory(destination);
			File.WriteAllText(Path.Combine(destination, "existing.txt"), "original");
			Assert.Throws<IOException>(() => ArchiveService.Extract(archivePath, destination));
			Assert.That(File.ReadAllText(Path.Combine(destination, "existing.txt")), Is.EqualTo("original"));
			Assert.That(File.Exists(Path.Combine(destination, "first.txt")), Is.False);
		}

		[Test]
		public void CaseCollidingEntriesAndFileFolderConflictsAreRejected()
		{
			MakeZip(("FILE.txt", "upper"), ("file.txt", "lower"));
			Assert.Throws<InvalidDataException>(() => ArchiveService.Extract(archivePath, destination));
			File.Delete(archivePath);
			MakeZip(("folder", "file"), ("folder/child.txt", "child"));
			Assert.Throws<InvalidDataException>(() => ArchiveService.Extract(archivePath, destination));
			Assert.That(Directory.Exists(destination), Is.False);
		}

		[Test]
		public void ExtractionSizeLimitRejectsOversizedArchivesBeforeWriting()
		{
			MakeZip(("too-large.txt", "1234567890"));
			Assert.Throws<InvalidDataException>(() => ArchiveService.Extract(archivePath, destination, maximumBytes: 5));
			Assert.That(Directory.Exists(destination), Is.False);
		}

		[Test]
		public void ArchiveCreationRefusesExistingOutputAndSelfInclusion()
		{
			string input = Path.Combine(root, "input.txt");
			File.WriteAllText(input, "data");
			ArchiveService.CreateZip([input], archivePath);
			byte[] original = File.ReadAllBytes(archivePath);
			Assert.Throws<IOException>(() => ArchiveService.CreateZip([input], archivePath));
			Assert.That(File.ReadAllBytes(archivePath), Is.EqualTo(original));
			Assert.Throws<IOException>(() => ArchiveService.CreateZip([root], Path.Combine(root, "inside.zip")));
			Assert.That(File.Exists(Path.Combine(root, "inside.zip")), Is.False);
		}

		[Test]
		public void DuplicateInputNamesAreRejectedWithoutCreatingAnArchive()
		{
			string left = Directory.CreateDirectory(Path.Combine(root, "left")).FullName;
			string right = Directory.CreateDirectory(Path.Combine(root, "right")).FullName;
			File.WriteAllText(Path.Combine(left, "same.txt"), "left");
			File.WriteAllText(Path.Combine(right, "same.txt"), "right");
			Assert.Throws<IOException>(() => ArchiveService.CreateZip([Path.Combine(left, "same.txt"), Path.Combine(right, "same.txt")], archivePath));
			Assert.That(File.Exists(archivePath), Is.False);
		}

		[Test]
		public void CancellationDuringCreationLeavesNoArchiveOrTemporaryFile()
		{
			string input = Path.Combine(root, "input.txt");
			File.WriteAllText(input, "data");
			using var cancellation = new CancellationTokenSource();
			var progress = new CallbackProgress(_ => cancellation.Cancel());
			Assert.Throws<OperationCanceledException>(() => ArchiveService.CreateZip([input], archivePath,
				cancellationToken: cancellation.Token, progress: progress));
			Assert.That(File.Exists(archivePath), Is.False);
			Assert.That(Directory.GetFiles(root, ".Software.Archiver-*.tmp"), Is.Empty);
		}

		[Test]
		public void TarArchiveIsListedAndExtractedWithRelativeEntryPaths()
		{
			string tarPath = Path.Combine(root, "sample.tar");
			using (var output = File.Create(tarPath))
			using (var writer = new TarWriter(output))
			{
				writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "./"));
				using var content = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("tar content"));
				writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "./file.txt") { DataStream = content });
			}
			Assert.That(ArchiveService.ReadArchive(tarPath).Any(entry => entry.Name.EndsWith("file.txt")), Is.True);
			ArchiveService.Extract(tarPath, destination);
			Assert.That(File.ReadAllText(Path.Combine(destination, "file.txt")), Is.EqualTo("tar content"));
		}

		[Test]
		public void TarSymbolicLinksAreRejected()
		{
			string tarPath = Path.Combine(root, "links.tar");
			using (var output = File.Create(tarPath))
			using (var writer = new TarWriter(output))
				writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "link") { LinkName = "../outside" });
			Assert.Throws<InvalidDataException>(() => ArchiveService.Extract(tarPath, destination));
			Assert.That(Directory.Exists(destination), Is.False);
		}

		[Test]
		public void MalformedArchiveFailsWithoutCreatingOutput()
		{
			File.WriteAllText(archivePath, "not an archive");
			Assert.Catch(() => ArchiveService.Extract(archivePath, destination));
			Assert.That(Directory.Exists(destination), Is.False);
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