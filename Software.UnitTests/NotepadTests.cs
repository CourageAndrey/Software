using System.Text.Json;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Xml;
using System.Xml.Linq;
using ICSharpCode.AvalonEdit.Folding;
using Software.Notepad;
using TextSearch = Software.Notepad.TextSearch;

namespace Software.UnitTests
{
	public class NotepadToolsTests
	{
		[Test]
		public void JsonFormattingAndMinificationPreservePreciseNumbers()
		{
			string text = "{\"number\":1.0000000000000000000001,\"huge\":123456789012345678901234567890,\"small\":1e-300}";
			string formatted = StructuredTextTools.FormatJson(text);
			Assert.That(formatted, Does.Contain("\n"));
			Assert.That(StructuredTextTools.FormatJson(formatted, compact: true), Is.EqualTo(text));
		}

		[Test]
		public void JsonSortOrdersNestedObjectsButKeepsArrayOrder()
		{
			string sorted = StructuredTextTools.FormatJson("{\"z\":[2,1,{\"b\":2,\"a\":1}],\"a\":0}", compact: true, sortKeys: true);
			Assert.That(sorted, Is.EqualTo("{\"a\":0,\"z\":[2,1,{\"a\":1,\"b\":2}]}"));
		}

		[TestCase("{\"a\":1,\"a\":2}")]
		[TestCase("{\"a\":1,}")]
		[TestCase("{/*comment*/\"a\":1}")]
		[TestCase("{bad}")]
		public void InvalidJsonAndDuplicateKeysAreRejected(string text)
			=> Assert.Catch<JsonException>(() => StructuredTextTools.ValidateJson(text));

		[Test]
		public void JsonPathSelectsMatchingValuesAndNulls()
		{
			string result = StructuredTextTools.QueryJson("{\"items\":[{\"price\":5,\"name\":\"low\"},{\"price\":20,\"name\":\"high\"}]}", "$.items[?@.price < 10].name");
			Assert.That(result, Does.Contain("low"));
			Assert.That(result, Does.Not.Contain("high"));
			Assert.That(StructuredTextTools.QueryJson("{\"value\":null}", "$.value"), Does.Contain("null"));
		}

		[Test]
		public void JsonTreeProvidesPathsForArraysAndEscapedPropertyNames()
		{
			var tree = StructuredTextTools.JsonTree("{\"a.b\":[1,2]}");
			string path = tree.Children[0].Children[1].Path;
			Assert.That(StructuredTextTools.QueryJson("{\"a.b\":[1,2]}", path), Does.EndWith("2"));
		}

		[Test]
		public void XmlFormattingAndCompactingPreserveMixedContentAndXmlSpace()
		{
			string text = "<root><p>Hello <b>world</b> !</p><data xml:space=\"preserve\">  <a/>  </data><blank> </blank></root>";
			string formatted = StructuredTextTools.FormatXml(text);
			string compact = StructuredTextTools.FormatXml(formatted, compact: true);
			var document = XDocument.Parse(compact, LoadOptions.PreserveWhitespace);
			Assert.That(document.Root!.Element("p")!.Value, Is.EqualTo("Hello world !"));
			Assert.That(document.Root.Element("data")!.Value, Is.EqualTo("    "));
			Assert.That(document.Root.Element("blank")!.Value, Is.EqualTo(" "));
			Assert.That(formatted, Does.Contain("\n"));
		}

		[Test]
		public void XmlDeclarationCommentsAndCdataArePreserved()
		{
			string text = "<?xml version=\"1.0\" encoding=\"utf-8\"?><root><!--comment--><value><![CDATA[x<y]]></value></root>";
			string formatted = StructuredTextTools.FormatXml(text);
			Assert.That(formatted, Does.Contain("encoding=\"utf-8\""));
			Assert.That(formatted, Does.Contain("<!--comment-->"));
			Assert.That(formatted, Does.Contain("<![CDATA[x<y]]>"));
		}

		[TestCase("<!DOCTYPE root [<!ENTITY secret SYSTEM 'file:///C:/secret'>]><root>&secret;</root>")]
		[TestCase("<root><unclosed></root>")]
		public void UnsafeOrInvalidXmlIsRejected(string text)
			=> Assert.Throws<XmlException>(() => StructuredTextTools.ValidateXml(text));

		[Test]
		public void XPathSupportsDefaultNamespacesAndScalarResults()
		{
			string text = "<root xmlns=\"urn:test\"><item>one</item><item>two</item></root>";
			Assert.That(StructuredTextTools.QueryXml(text, "count(/d:root/d:item)"), Is.EqualTo("2"));
			Assert.That(StructuredTextTools.QueryXml(text, "/d:root/d:item[2]/text()"), Is.EqualTo("two"));
			var node = StructuredTextTools.XmlTree(text).Children[1];
			Assert.That(StructuredTextTools.QueryXml(text, node.Path), Does.Contain("two"));
		}

		[Test]
		public void XsdValidationReportsInvalidValues()
		{
			string schema = "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:element name=\"value\" type=\"xs:int\"/></xs:schema>";
			Assert.That(StructuredTextTools.ValidateXmlSchema("<value>12</value>", schema), Is.Empty);
			Assert.That(StructuredTextTools.ValidateXmlSchema("<value>bad</value>", schema), Is.Not.Empty);
		}
	}

	public class NotepadSearchTests
	{
		[Test]
		public void SearchWrapsAndRespectsCaseAndWholeWords()
		{
			var match = TextSearch.Find("word wording WORD", "word", 6, matchCase: true, wholeWord: true);
			Assert.That(match.Index, Is.EqualTo(0));
			Assert.That(TextSearch.Find("word wording WORD", "word", 6, wholeWord: true).Index, Is.EqualTo(13));
		}

		[Test]
		public void LiteralReplacementDoesNotInterpretDollarSigns()
		{
			var result = TextSearch.ReplaceAll("a a", "a", "$1");
			Assert.That(result.Text, Is.EqualTo("$1 $1"));
			Assert.That(result.Count, Is.EqualTo(2));
		}

		[Test]
		public void RegexReplacementSupportsCaptureGroups()
		{
			Assert.That(TextSearch.ReplaceAll("a12 b34", @"([a-z])(\d+)", "$2$1", regex: true).Text, Is.EqualTo("12a 34b"));
			Assert.That(TextSearch.Pattern("(a+)+$", regex: true).MatchTimeout, Is.EqualTo(TimeSpan.FromMilliseconds(250)));
		}
	}

	public class NotepadFileTests
	{
		private string root = null!;
		[SetUp]
		public void Setup() => root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"NotepadFileTests-{Guid.NewGuid():N}")).FullName;
		[TearDown]
		public void TearDown() => Directory.Delete(root, recursive: true);

		[TestCase(0)]
		[TestCase(1)]
		[TestCase(2)]
		[TestCase(3)]
		public void FileRoundTripsPreserveBomEncodingAndLineEndings(int format)
		{
			Encoding encoding = format switch
			{
				1 => new UTF8Encoding(true, true), 2 => new UnicodeEncoding(false, true, true),
				3 => new UnicodeEncoding(true, true, true), _ => new UTF8Encoding(false, true)
			};
			string path = Path.Combine(root, "file.txt");
			string text = "first\r\nlast\n";
			string hash = NotepadFile.Save(path, text, encoding);
			var file = NotepadFile.Read(path);
			Assert.That(file.Text, Is.EqualTo(text));
			Assert.That(file.Hash, Is.EqualTo(hash));
			Assert.That(file.Encoding.GetPreamble(), Is.EqualTo(encoding.GetPreamble()));
			NotepadFile.Save(Path.Combine(root, "copy.txt"), file.Text, file.Encoding);
			Assert.That(File.ReadAllBytes(Path.Combine(root, "copy.txt")), Is.EqualTo(File.ReadAllBytes(path)));
		}

		[Test]
		public void FileWritesRefuseUnrequestedOverwriteAndLeaveNoTemporaryFiles()
		{
			string path = Path.Combine(root, "file.txt");
			NotepadFile.Save(path, "old", new UTF8Encoding(false));
			Assert.Throws<IOException>(() => NotepadFile.Save(path, "new", new UTF8Encoding(false)));
			Assert.That(File.ReadAllText(path), Is.EqualTo("old"));
			NotepadFile.Save(path, "new", new UTF8Encoding(false), overwrite: true);
			Assert.That(File.ReadAllText(path), Is.EqualTo("new"));
			Assert.That(Directory.GetFiles(root, ".Software.Notepad-*.tmp"), Is.Empty);
		}

		[Test]
		public void BinaryInputIsRejected()
		{
			string path = Path.Combine(root, "binary");
			File.WriteAllBytes(path, [65, 0, 66]);
			Assert.Throws<InvalidDataException>(() => NotepadFile.Read(path));
		}
	}

	[Apartment(ApartmentState.STA)]
	public class NotepadUiTests
	{
		private MainWindow window = null!;
		private string root = null!;
		private ListBox Tabs => (ListBox)window.FindName("TabStrip");
		private TextBox Results => (TextBox)window.FindName("QueryResults");

		[SetUp]
		public void Setup()
		{
			root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"NotepadUiTests-{Guid.NewGuid():N}")).FullName;
			window = new MainWindow();
			window.Show();
		}

		[TearDown]
		public void TearDown()
		{
			foreach (var document in Tabs.Items.Cast<EditorDocument>())
				document.MarkSaved(Path.Combine(root, "discard.txt"), "test-only");
			window.Close();
			Directory.Delete(root, recursive: true);
		}

		[Test]
		public void TabSwitchingPreservesTextAndUndoHistory()
		{
			var first = window.ActiveDocument!;
			first.Document.Insert(0, "first");
			var second = window.NewDocument();
			second.Document.Insert(0, "second");
			Tabs.SelectedItem = first;
			Assert.That(first.Text, Is.EqualTo("first"));
			Assert.That(second.Visibility, Is.EqualTo(Visibility.Collapsed));
			first.Undo();
			Assert.That(first.Text, Is.Empty);
			Assert.That(second.Text, Is.EqualTo("second"));
		}

		[Test]
		public void JsonFormattingIsUndoableAndBuildsFoldings()
		{
			RunOnDispatcher(async () =>
			{
				var document = window.NewDocument("{\"a\":{\"b\":1}}");
				Assert.That(await window.RunToolAsync("JsonFormat"), Is.True);
				Assert.That(document.Text, Does.Contain("\n"));
				Assert.That(document.Foldings.AllFoldings, Is.Not.Empty);
				document.Undo();
				Assert.That(document.Text, Is.EqualTo("{\"a\":{\"b\":1}}"));
				Assert.That(document.IsDirty, Is.False);
			});
		}

		[Test]
		public void InvalidStructuredTextShowsDiagnosticsWithoutChangingTheDocument()
		{
			RunOnDispatcher(async () =>
			{
				var document = window.NewDocument("{invalid}");
				Assert.That(await window.RunToolAsync("JsonFormat"), Is.False);
				Assert.That(document.Text, Is.EqualTo("{invalid}"));
				Assert.That(Results.Text, Is.Not.Empty);
			});
		}

		[Test]
		public void JsonTreeAndQueryUseTheActiveDocument()
		{
			RunOnDispatcher(async () =>
			{
				window.NewDocument("{\"items\":[1,2]}");
				Assert.That(await window.RunToolAsync("JsonTree"), Is.True);
				Assert.That(((TreeView)window.FindName("StructureTree")).Items.Count, Is.EqualTo(1));
				((TextBox)window.FindName("QueryBox")).Text = "$.items[1]";
				((ComboBox)window.FindName("QueryMode")).SelectedIndex = 0;
				Assert.That(await window.RunQueryAsync(), Is.True);
				Assert.That(Results.Text, Does.EndWith("2"));
			});
		}

		[Test]
		public void FormattingASelectionDoesNotModifySurroundingText()
		{
			RunOnDispatcher(async () =>
			{
				var document = window.NewDocument("prefix {\"b\":2,\"a\":1} suffix");
				document.Select(7, 13);
				Assert.That(await window.RunToolAsync("JsonSort"), Is.True);
				Assert.That(document.Text, Does.StartWith("prefix ").And.EndWith(" suffix"));
				Assert.That(document.Text.IndexOf("\"a\"", StringComparison.Ordinal), Is.LessThan(document.Text.IndexOf("\"b\"", StringComparison.Ordinal)));
			});
		}

		[Test]
		public void FindStartsAtTheCaretAndReplaceAllIsUndoable()
		{
			var document = window.NewDocument("one one");
			((TextBox)window.FindName("FindBox")).Text = "one";
			((Button)window.FindName("FindNextButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Assert.That(document.SelectionStart, Is.EqualTo(0));
			((TextBox)window.FindName("ReplaceBox")).Text = "two";
			((Button)window.FindName("ReplaceAllButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Assert.That(document.Text, Is.EqualTo("two two"));
			document.Undo();
			Assert.That(document.Text, Is.EqualTo("one one"));
		}

		[Test]
		public void OpenAndSavePreserveEncodingAndDoNotDuplicateTabs()
		{
			RunOnDispatcher(async () =>
			{
				string path = Path.Combine(root, "file.xml");
				NotepadFile.Save(path, "<root/>\r\n", new UnicodeEncoding(false, true, true));
				var document = await window.OpenFileAsync(path);
				Assert.That(document, Is.Not.Null);
				Assert.That(document!.SyntaxLanguage, Is.EqualTo("XML"));
				Assert.That(Tabs.Items.Count, Is.EqualTo(1));
				Assert.That(await window.OpenFileAsync(path), Is.SameAs(document));
				Assert.That(Tabs.Items.Count, Is.EqualTo(1));
				document.Document.Insert(document.Text.Length, "<extra/>");
				Assert.That(await window.SaveDocumentAsync(document), Is.True);
				Assert.That(document.IsDirty, Is.False);
				var saved = NotepadFile.Read(path);
				Assert.That(saved.Text, Is.EqualTo(document.Text));
				Assert.That(saved.Encoding.GetPreamble(), Is.EqualTo(new UnicodeEncoding(false, true, true).GetPreamble()));
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
			Assert.That(task.IsCompleted, Is.True, "Notepad operation did not finish within 15 seconds.");
			task.GetAwaiter().GetResult();
		}
	}
}