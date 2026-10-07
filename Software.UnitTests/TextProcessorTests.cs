using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Software.TextProcessor;

namespace Software.UnitTests
{
	[Apartment(ApartmentState.STA)]
	public class TextProcessorFileTests
	{
		private string _root = null!;
		[SetUp]
		public void Setup() => _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"TextProcessorTests-{Guid.NewGuid():N}")).FullName;
		[TearDown]
		public void TearDown() => Directory.Delete(_root, recursive: true);

		private static string Text(FlowDocument document) => new TextRange(document.ContentStart, document.ContentEnd).Text;

		[TestCase(".docx")]
		[TestCase(".rtf")]
		public void RichTextRoundTripsPreserveTextAndCharacterFormatting(string extension)
		{
			var document = DocumentFiles.NewDocument();
			var run = new Run("formatted text") { FontWeight = FontWeights.Bold, FontStyle = FontStyles.Italic, FontSize = 24,
				TextDecorations = TextDecorations.Underline, Foreground = Brushes.Red };
			document.Blocks.Add(new Paragraph(run) { TextAlignment = TextAlignment.Center });
			string path = Path.Combine(_root, "rich" + extension);
			DocumentFiles.Save(path, document);
			var opened = DocumentFiles.Open(path).Document;
			Assert.That(Text(opened), Does.Contain("formatted text"));
			var paragraph = (Paragraph)opened.Blocks.FirstBlock;
			var range = new TextRange(paragraph.ContentStart, paragraph.ContentEnd);
			Assert.That(range.GetPropertyValue(TextElement.FontWeightProperty), Is.EqualTo(FontWeights.Bold));
			Assert.That(range.GetPropertyValue(TextElement.FontStyleProperty), Is.EqualTo(FontStyles.Italic));
			Assert.That(paragraph.TextAlignment, Is.EqualTo(TextAlignment.Center));
			if (extension == ".docx")
			{
				AssertValidDocx(path);
			}
		}

		[Test]
		public void DocxRoundTripPreservesTablesListsAndEmbeddedImages()
		{
			var document = DocumentFiles.NewDocument();
			var list = new System.Windows.Documents.List { MarkerStyle = TextMarkerStyle.Decimal };
			list.ListItems.Add(new ListItem(new Paragraph(new Run("first item"))));
			list.ListItems.Add(new ListItem(new Paragraph(new Run("second item"))));
			document.Blocks.Add(list);
			var table = new Table();
			var group = new TableRowGroup();
			var row = new TableRow();
			row.Cells.Add(new TableCell(new Paragraph(new Run("cell one"))));
			row.Cells.Add(new TableCell(new Paragraph(new Run("cell two"))));
			group.Rows.Add(row); table.RowGroups.Add(group); document.Blocks.Add(table);
			var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
			bitmap.Freeze();
			document.Blocks.Add(new Paragraph(new InlineUIContainer(new Image { Source = bitmap, Width = 20 })));
			string path = Path.Combine(_root, "objects.docx");
			DocumentFiles.Save(path, document);
			AssertValidDocx(path);
			var opened = DocumentFiles.Open(path).Document;
			Assert.That(opened.Blocks.OfType<System.Windows.Documents.List>().Single().ListItems.Count, Is.EqualTo(2));
			Assert.That(opened.Blocks.OfType<Table>().Single().RowGroups[0].Rows[0].Cells.Count, Is.EqualTo(2));
			Assert.That(opened.Blocks.OfType<Paragraph>().SelectMany(paragraph => paragraph.Inlines).OfType<InlineUIContainer>().Single().Child, Is.TypeOf<Image>());
			Assert.That(Text(opened), Does.Contain("cell one"));
		}

		[Test]
		public void LegacyBinaryDocImportsActualText()
		{
			string path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "simple.doc");
			Assert.That(File.Exists(path), Is.True, "The Apache POI DOC fixture must be copied to the test output.");
			var loaded = DocumentFiles.Open(path);
			Assert.That(Text(loaded.Document), Does.Contain("simple"));
			Assert.That(loaded.ImportNote, Does.Contain("imported as text"));
		}

		[Test]
		public void PlainTextCanBeOpenedAndSaved()
		{
			string path = Path.Combine(_root, "plain.txt");
			File.WriteAllText(path, "first\nsecond");
			var loaded = DocumentFiles.Open(path);
			Assert.That(Text(loaded.Document), Does.Contain("first").And.Contain("second"));
			DocumentFiles.Save(Path.Combine(_root, "copy.txt"), loaded.Document);
			Assert.That(File.ReadAllText(Path.Combine(_root, "copy.txt")), Does.Contain("first"));
		}

		[Test]
		public void SaveRefusesUnrequestedOverwriteAndLegacyDocOutput()
		{
			string path = Path.Combine(_root, "file.rtf");
			var document = DocumentFiles.NewDocument();
			document.Blocks.Add(new Paragraph(new Run("original")));
			DocumentFiles.Save(path, document);
			byte[] original = File.ReadAllBytes(path);
			Assert.Throws<IOException>(() => DocumentFiles.Save(path, document));
			Assert.That(File.ReadAllBytes(path), Is.EqualTo(original));
			Assert.Throws<NotSupportedException>(() => DocumentFiles.Save(Path.Combine(_root, "file.doc"), document));
			Assert.That(Directory.GetFiles(_root, ".Software.TextProcessor-*.tmp"), Is.Empty);
		}

		[Test]
		public void InvalidWordDocumentsFailInsteadOfOpeningAsGibberish()
		{
			string doc = Path.Combine(_root, "invalid.doc");
			string docx = Path.Combine(_root, "invalid.docx");
			File.WriteAllText(doc, "not a Word document");
			File.WriteAllText(docx, "not a Word document");
			Assert.Catch(() => DocumentFiles.Open(doc));
			Assert.Catch(() => DocumentFiles.Open(docx));
		}

		private static void AssertValidDocx(string path)
		{
			using var document = WordprocessingDocument.Open(path, false);
			var errors = new OpenXmlValidator().Validate(document).Select(error => error.Description).Take(10).ToArray();
			Assert.That(errors, Is.Empty, string.Join(Environment.NewLine, errors));
		}
	}

	[Apartment(ApartmentState.STA)]
	public class TextProcessorUiTests
	{
		private MainWindow _window = null!;
		private string _root = null!;
		private RichTextBox Editor => (RichTextBox)_window.FindName("Editor");
		private string Text => new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd).Text;

		[SetUp]
		public void Setup()
		{
			_root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"TextProcessorUiTests-{Guid.NewGuid():N}")).FullName;
			_window = new MainWindow(); _window.Show();
		}

		[TearDown]
		public void TearDown()
		{
			RunOnDispatcher(async () => Assert.That(await _window.SaveDocumentAsync(chosenPath: Path.Combine(_root, "cleanup.rtf")), Is.True));
			_window.Close(); Directory.Delete(_root, recursive: true);
		}

		[Test]
		public void BoldFormattingIsUndoableAndTrackedAsUnsaved()
		{
			Editor.Selection.Text = "sample";
			Editor.SelectAll(); Editor.Focus();
			((ToggleButton)_window.FindName("BoldButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
			Assert.That(Editor.Selection.GetPropertyValue(TextElement.FontWeightProperty), Is.EqualTo(FontWeights.Bold));
			Assert.That(_window.IsDirty, Is.True);
			Editor.Undo();
			Assert.That(Editor.Selection.GetPropertyValue(TextElement.FontWeightProperty), Is.Not.EqualTo(FontWeights.Bold));
		}

		[Test]
		public void FindAndReplaceWorkAcrossFormattingRunsAndUndoTogether()
		{
			Editor.Document.Blocks.Clear();
			Editor.Document.Blocks.Add(new Paragraph(new Run("one ")) { Inlines = { new Bold(new Run("one")) } });
			((TextBox)_window.FindName("FindBox")).Text = "one";
			((Button)_window.FindName("FindNextButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Assert.That(Editor.Selection.Text, Is.EqualTo("one"));
			((TextBox)_window.FindName("ReplaceBox")).Text = "two";
			((Button)_window.FindName("ReplaceAllButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Assert.That(Text.Trim(), Is.EqualTo("two two"));
			Editor.Undo();
			Assert.That(Text.Trim(), Is.EqualTo("one one"));
		}

		[Test]
		public void DocumentOpenSaveAndTableInsertionWorkInTheEditor()
		{
			RunOnDispatcher(async () =>
			{
				var original = DocumentFiles.NewDocument();
				original.Blocks.Add(new Paragraph(new Run("loaded")));
				string path = Path.Combine(_root, "source.docx");
				DocumentFiles.Save(path, original);
				Assert.That(await _window.OpenDocumentAsync(path), Is.True);
				Assert.That(_window.IsDirty, Is.False);
				Assert.That(Text, Does.Contain("loaded"));
				_window.InsertTable(2, 3);
				Assert.That(Editor.Document.Blocks.OfType<Table>().Single().RowGroups[0].Rows.Count, Is.EqualTo(2));
				Assert.That(await _window.SaveDocumentAsync(chosenPath: Path.Combine(_root, "result.docx")), Is.True);
				Assert.That(_window.IsDirty, Is.False);
				Assert.That(DocumentFiles.Open(Path.Combine(_root, "result.docx")).Document.Blocks.OfType<Table>(), Is.Not.Empty);
			});
		}

		private void RunOnDispatcher(Func<Task> action)
		{
			Task task = _window.Dispatcher.InvokeAsync(action).Task.Unwrap();
			var frame = new DispatcherFrame();
			var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
			timeout.Tick += (_, _) => frame.Continue = false;
			_ = task.ContinueWith(_ => _window.Dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
			timeout.Start(); Dispatcher.PushFrame(frame); timeout.Stop();
			Assert.That(task.IsCompleted, Is.True, "Text Processor did not finish within 15 seconds.");
			task.GetAwaiter().GetResult();
		}
	}
}