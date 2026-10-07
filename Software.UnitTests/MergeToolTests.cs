using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using Software.MergeTool;

namespace Software.UnitTests
{
	public class MergeEngineTests
	{
		[Test]
		public void IdenticalFilesHaveNoDifferences()
		{
			var comparison = MergeComparison.Compare("first\r\nsecond\r\n", "first\r\nsecond\r\n");
			Assert.That(comparison.Differences, Is.Empty);
		}

		[TestCase("", "added")]
		[TestCase("removed", "")]
		[TestCase("first\nlast", "first\ninserted\nlast")]
		[TestCase("first\nremoved\nlast", "first\nlast")]
		[TestCase("old\ncommon\nolder\n", "new\ncommon\nnewer\n")]
		[TestCase("last", "last\n")]
		[TestCase("last\n", "last")]
		[TestCase("first\r\nlast\r\n", "first\nlast\n")]
		[TestCase("a\rb\r", "a\rc\r")]
		[TestCase("a\n\n", "a\n")]
		[TestCase(" spaced ", "spaced")]
		public void ApplyingAllRightBlocksExactlyReproducesTheRightText(string left, string right)
		{
			var comparison = MergeComparison.Compare(left, right);
			Assert.That(comparison.Differences, Is.Not.Empty);
			string result = left;
			foreach (var block in comparison.Differences.Reverse())
			{
				Assert.That(result.Substring(block.LeftOffset, block.LeftText.Length), Is.EqualTo(block.LeftText));
				result = result.Remove(block.LeftOffset, block.LeftText.Length).Insert(block.LeftOffset, block.RightText);
			}
			Assert.That(result, Is.EqualTo(right));
		}

		[Test]
		public void InsertionsAlignWithPlaceholderRowsAndKeepOriginalLineNumbers()
		{
			var comparison = MergeComparison.Compare("first\nlast", "first\ninserted\nlast");
			Assert.That(comparison.Rows.Single(row => row.RightText == "inserted").LeftLine, Is.Null);
			Assert.That(comparison.Rows.Single(row => row.LeftText == "last").LeftLine, Is.EqualTo(2));
		}

		[Test]
		public void UseBothSeparatesNonTerminatedLines()
		{
			Assert.That(MergeComparison.Compare("left", "right").Differences.Single().BothText, Is.EqualTo("left\nright"));
		}

		[Test]
		public void UnrelatedChangesRemainSeparateBlocks()
		{
			Assert.That(MergeComparison.Compare("old\ncommon\nold", "new\ncommon\nnew").Differences, Has.Length.EqualTo(2));
		}

		[Test]
		public void VariedLineEditsRoundTripExactly()
		{
			var random = new Random(17);
			for (int iteration = 0; iteration < 200; iteration++)
			{
				string left = GenerateText();
				string right = GenerateText();
				string result = left;
				foreach (var block in MergeComparison.Compare(left, right).Differences.Reverse())
				{
					result = result.Remove(block.LeftOffset, block.LeftText.Length).Insert(block.LeftOffset, block.RightText);
				}

				Assert.That(result, Is.EqualTo(right), $"Round trip {iteration}");
			}

			string GenerateText()
			{
				string[] endings = ["\n", "\r\n", "\r"];
				return string.Join(endings[random.Next(endings.Length)], Enumerable.Range(0, random.Next(0, 20))
					.Select(_ => new[] { "first", "second", "", " spaced ", "last" }[random.Next(5)]));
			}
		}
	}

	public class MergeFileTests
	{
		private string _root = null!;

		[SetUp]
		public void Setup() => _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"MergeFileTests-{Guid.NewGuid():N}")).FullName;

		[TearDown]
		public void TearDown() => Directory.Delete(_root, recursive: true);

		[TestCase(false)]
		[TestCase(true)]
		public void Utf8RoundTripPreservesBomAndLineEndings(bool bom)
		{
			string path = Path.Combine(_root, "file.txt");
			TextFileDocument.Save(path, "first\r\nlast", new UTF8Encoding(bom, true));
			byte[] original = File.ReadAllBytes(path);
			var document = TextFileDocument.Read(path);
			TextFileDocument.Save(Path.Combine(_root, "copy.txt"), document.Text, document.Encoding);
			Assert.That(File.ReadAllBytes(Path.Combine(_root, "copy.txt")), Is.EqualTo(original));
		}

		[TestCase(false)]
		[TestCase(true)]
		public void Utf16RoundTripPreservesEncoding(bool bigEndian)
		{
			string path = Path.Combine(_root, "file.txt");
			TextFileDocument.Save(path, "sample\ntext", new UnicodeEncoding(bigEndian, true, true));
			var document = TextFileDocument.Read(path);
			Assert.That(document.Text, Is.EqualTo("sample\ntext"));
			Assert.That(document.Encoding.GetPreamble(), Is.EqualTo(new UnicodeEncoding(bigEndian, true, true).GetPreamble()));
		}

		[Test]
		public void BinaryAndInvalidUtf8AreRejected()
		{
			string path = Path.Combine(_root, "binary");
			File.WriteAllBytes(path, [65, 0, 66]);
			Assert.Throws<InvalidDataException>(() => TextFileDocument.Read(path));
			File.WriteAllBytes(path, [0xFF, 65]);
			Assert.Throws<DecoderFallbackException>(() => TextFileDocument.Read(path));
		}

		[Test]
		public void SaveRefusesUnrequestedOverwriteAndCleansUpTemporaryFiles()
		{
			string path = Path.Combine(_root, "file.txt");
			File.WriteAllText(path, "original");
			Assert.Throws<IOException>(() => TextFileDocument.Save(path, "changed", new UTF8Encoding(false)));
			Assert.That(File.ReadAllText(path), Is.EqualTo("original"));
			TextFileDocument.Save(path, "changed", new UTF8Encoding(false), overwrite: true);
			Assert.That(File.ReadAllText(path), Is.EqualTo("changed"));
			Assert.That(Directory.GetFiles(_root, ".Software.MergeTool-*.tmp"), Is.Empty);
		}
	}

	[Apartment(ApartmentState.STA)]
	public class MergeToolUiTests
	{
		private MainWindow _window = null!;
		private string _root = null!;
		private TextEditor Result => (TextEditor)_window.FindName("ResultEditor");

		[SetUp]
		public void Setup()
		{
			_root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"MergeUiTests-{Guid.NewGuid():N}")).FullName;
			_window = new MainWindow();
		}

		[TearDown]
		public void TearDown()
		{
			while (Result.CanUndo)
			{
				Result.Undo();
			}

			((ComboBox)_window.FindName("EncodingBox")).SelectedIndex = 0;
			_window.Close();
			Directory.Delete(_root, recursive: true);
		}

		private void Click(string name) => ((Button)_window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

		private async Task LoadAsync(string left, string right)
		{
			string leftPath = Path.Combine(_root, "left.txt");
			string rightPath = Path.Combine(_root, "right.txt");
			File.WriteAllText(leftPath, left);
			File.WriteAllText(rightPath, right);
			_window.Show();
			Assert.That(await _window.LoadComparisonAsync(leftPath, rightPath), Is.True);
			await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
		}

		[Test]
		public void SourcesAreAlignedReadOnlyAndResultStartsFromLeft()
		{
			RunOnDispatcher(async () =>
			{
				await LoadAsync("first\nlast", "first\ninserted\nlast");
				var left = (TextEditor)_window.FindName("LeftEditor");
				var right = (TextEditor)_window.FindName("RightEditor");
				Assert.That(left.IsReadOnly && right.IsReadOnly, Is.True);
				Assert.That(left.Document.LineCount, Is.EqualTo(right.Document.LineCount));
				Assert.That(left.Text, Is.EqualTo("first\n\nlast"));
				Assert.That(Result.Text, Is.EqualTo("first\nlast"));
				Assert.That(left.ActualWidth, Is.GreaterThan(340));
				Assert.That(right.ActualWidth, Is.GreaterThan(340));
			});
		}

		[Test]
		public void ApplyingRightIsUndoableAndRedoable()
		{
			RunOnDispatcher(async () =>
			{
				await LoadAsync("old", "new");
				Click("UseRightButton");
				Assert.That(Result.Text, Is.EqualTo("new"));
				Assert.That(_window.Title, Does.StartWith("*"));
				Result.Undo();
				Assert.That(Result.Text, Is.EqualTo("old"));
				Assert.That(_window.Title, Does.Not.StartWith("*"));
				Result.Redo();
				Assert.That(Result.Text, Is.EqualTo("new"));
				Result.Undo();
				Click("UseRightButton");
				Assert.That(Result.Text, Is.EqualTo("new"));
			});
		}

		[Test]
		public void LaterDifferencesStayAnchoredAfterEarlierLengthChanges()
		{
			RunOnDispatcher(async () =>
			{
				string left = "one\nshared\ntwo";
				string right = "expanded\nline\nshared\nreplaced";
				await LoadAsync(left, right);
				Click("UseRightButton");
				Click("NextButton");
				Click("UseRightButton");
				Assert.That(Result.Text, Is.EqualTo(right));
				Click("UseLeftButton");
				Click("PreviousButton");
				Click("UseLeftButton");
				Assert.That(Result.Text, Is.EqualTo(left));
			});
		}

		[Test]
		public void ManualEditsOutsideTheDifferenceArePreserved()
		{
			RunOnDispatcher(async () =>
			{
				await LoadAsync("common\nold\ncommon", "common\nnew\ncommon");
				Result.Document.Insert(0, "prefix\n");
				Click("UseRightButton");
				Assert.That(Result.Text, Is.EqualTo("prefix\ncommon\nnew\ncommon"));
			});
		}

		[Test]
		public void BothChoiceKeepsBothSourceVersions()
		{
			RunOnDispatcher(async () =>
			{
				await LoadAsync("left", "right");
				Click("UseBothButton");
				Assert.That(Result.Text, Is.EqualTo("left\nright"));
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
			Assert.That(task.IsCompleted, Is.True, "Merge UI did not finish within 15 seconds.");
			task.GetAwaiter().GetResult();
		}
	}
}