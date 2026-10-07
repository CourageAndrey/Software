using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Software.Sheets;

namespace Software.UnitTests
{
	public class SheetsBookTests
	{
		private string _root = null!;
		[SetUp] public void Setup() => _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"SheetsTests-{Guid.NewGuid():N}")).FullName;
		[TearDown] public void TearDown() => Directory.Delete(_root, recursive: true);

		[TestCase(false)]
		[TestCase(true)]
		public void XlsAndXlsxRoundTripsPreserveTypedCellsFormulasAndStyles(bool legacy)
		{
			using var book = SpreadsheetBook.New(legacy);
			book.Edit([new(0, 0, "2"), new(1, 0, "3"), new(2, 0, "=SUM(A1:A2)"), new(0, 1, "TRUE"), new(1, 1, "'00123")]);
			book.Format([(0, 0)], bold: true, numberFormat: "0.00");
			Assert.That(book.Display(2, 0), Is.EqualTo("5"));
			string path = Path.Combine(_root, "book" + book.Extension);
			book.Save(path);
			Assert.That(book.IsDirty, Is.False);
			using var opened = SpreadsheetBook.Open(path);
			Assert.That(opened.Input(2, 0), Is.EqualTo("=SUM(A1:A2)"));
			Assert.That(opened.Display(2, 0), Is.EqualTo("5"));
			Assert.That(opened.Input(0, 1), Is.EqualTo("TRUE"));
			Assert.That(opened.Input(1, 1), Is.EqualTo("00123"));
			Assert.That(opened.Display(0, 0), Is.EqualTo("2.00"));
			Assert.That(opened.Appearance(0, 0).Bold, Is.True);
		}

		[Test]
		public void UndoRedoRestoresValuesAndDirtyState()
		{
			using var book = SpreadsheetBook.New();
			book.Edit([new(0, 0, "value")]);
			book.Undo(); Assert.That(book.Input(0, 0), Is.Empty); Assert.That(book.IsDirty, Is.False);
			book.Redo(); Assert.That(book.Input(0, 0), Is.EqualTo("value")); Assert.That(book.IsDirty, Is.True);
			book.Format([(0, 0)], bold: true); book.Undo(); Assert.That(book.Appearance(0, 0).Bold, Is.False);
		}

		[Test]
		public void InvalidFormulaRejectsEntireBatchBeforeEditing()
		{
			using var book = SpreadsheetBook.New();
			Assert.Catch(() => book.Edit([new(0, 0, "changed"), new(1, 0, "=SUM(")]));
			Assert.That(book.Input(0, 0), Is.Empty); Assert.That(book.IsDirty, Is.False);
		}

		[Test]
		public void WorksheetManagementAndReferencesPersist()
		{
			using var book = SpreadsheetBook.New();
			book.Edit([new(0, 0, "12")]); book.AddSheet("Other");
			book.Edit([new(0, 0, "=Sheet1!A1*2")]);
			Assert.That(book.Display(0, 0), Is.EqualTo("24"));
			book.RenameSheet("Renamed");
			string path = Path.Combine(_root, "multi.xlsx"); book.Save(path);
			using var opened = SpreadsheetBook.Open(path);
			Assert.That(opened.SheetNames, Is.EqualTo(new[] { "Sheet1", "Renamed" }));
			opened.SelectSheet(1); Assert.That(opened.Display(0, 0), Is.EqualTo("24"));
			opened.DeleteSheet(); Assert.Throws<InvalidOperationException>(opened.DeleteSheet);
		}

		[Test]
		public void SavesRefuseUnexpectedOverwriteAndWrongFormat()
		{
			using var book = SpreadsheetBook.New();
			string path = Path.Combine(_root, "book.xlsx"); book.Save(path);
			Assert.Throws<IOException>(() => book.Save(path));
			Assert.Throws<NotSupportedException>(() => book.Save(Path.Combine(_root, "book.xls")));
			Assert.That(Directory.GetFiles(_root, ".Software.Sheets-*.tmp"), Is.Empty);
		}

		[Test]
		public void CellAddressesRoundTripAndEnforceEditorLimits()
		{
			Assert.That(SpreadsheetBook.Address(9, 27), Is.EqualTo("AB10"));
			Assert.That(SpreadsheetBook.ParseAddress("AB10"), Is.EqualTo((9, 27)));
			Assert.Throws<ArgumentOutOfRangeException>(() => SpreadsheetBook.ParseAddress("IW1"));
		}

		[Test]
		public void LiteralStringsStayStringsWhenEditedAndCopied()
		{
			using var book = SpreadsheetBook.New();
			book.Edit([new(0, 0, "'00123"), new(0, 1, "'=literal"), new(0, 2, "'TRUE")]);
			Assert.That(book.EditableInput(0, 0), Is.EqualTo("'00123"));
			Assert.That(book.EditableInput(0, 1), Is.EqualTo("'=literal"));
			book.Edit([new(1, 0, book.EditableInput(0, 0)), new(1, 1, book.EditableInput(0, 1))]);
			Assert.That(book.Display(1, 0), Is.EqualTo("00123"));
			Assert.That(book.Display(1, 1), Is.EqualTo("=literal"));
		}

		[Test]
		public void ClipboardHandlesQuotedTabsNewlinesAndRejectsOutOfBoundsRanges()
		{
			string value = "quoted \"text\"\nwith\ttabs";
			var edits = ClipboardCells.Parse(ClipboardCells.Encode(value) + "\t42", 0, 0);
			Assert.That(edits, Has.Length.EqualTo(2));
			Assert.That(edits[0].Input, Is.EqualTo(value));
			Assert.That(edits[1].Input, Is.EqualTo("42"));
			Assert.Throws<ArgumentOutOfRangeException>(() => ClipboardCells.Parse("first\tsecond", 0, 255));
		}

		[Test]
		public void ClipboardBlankRowsKeepTheirOriginalPositions()
		{
			var edits = ClipboardCells.Parse("\nfirst\n\nlast", 5, 0);
			Assert.That(edits.Select(edit => edit.Row), Is.EqualTo(new[] { 5, 6, 7, 8 }));
			Assert.That(edits.Select(edit => edit.Input), Is.EqualTo(new[] { "", "first", "", "last" }));
			Assert.That(ClipboardCells.Parse(ClipboardCells.Encode(""), 0, 0).Single().Input, Is.Empty);
		}
	}

	[Apartment(ApartmentState.STA)]
	public class SheetsUiTests
	{
		private MainWindow _window = null!;
		private string _root = null!;
		private DataGrid Grid => (DataGrid)_window.FindName("CellGrid");
		[SetUp]
		public void Setup()
		{
			_root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"SheetsUiTests-{Guid.NewGuid():N}")).FullName;
			_window = new MainWindow(); _window.Show();
		}
		[TearDown]
		public void TearDown()
		{
			RunOnDispatcher(async () => Assert.That(await _window.SaveWorkbookAsync(chosenPath: Path.Combine(_root, "cleanup" + _window.Book.Extension)), Is.True));
			_window.Close(); Directory.Delete(_root, recursive: true);
		}
		private void Click(string name) => ((Button)_window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

		[Test]
		public void FormulaBarEditsEvaluateAndCanBeUndone()
		{
			((TextBox)_window.FindName("FormulaBox")).Text = "=2+3";
			Click("ApplyFormulaButton");
			Assert.That(_window.Book.Input(0, 0), Is.EqualTo("=2+3"));
			Assert.That(_window.Book.Display(0, 0), Is.EqualTo("5"));
			Assert.That(_window.Title, Does.StartWith("*"));
			Click("UndoButton"); Assert.That(_window.Book.Input(0, 0), Is.Empty);
			Click("RedoButton"); Assert.That(_window.Book.Display(0, 0), Is.EqualTo("5"));
		}

		[Test]
		public void DirectGridEditsCommitTypedValues()
		{
			RunOnDispatcher(async () =>
			{
				_window.SelectCell(0, 0); Grid.Focus();
				await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
				Assert.That(Grid.BeginEdit(), Is.True);
				Grid.UpdateLayout();
				var input = FindTextBox(Grid.Columns[0].GetCellContent(Grid.Items[0]));
				Assert.That(input, Is.Not.Null);
				input!.Text = "42";
				Assert.That(Grid.CommitEdit(DataGridEditingUnit.Cell, true), Is.True);
				await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
				Assert.That(_window.Book.Input(0, 0), Is.EqualTo("42"));
				Assert.That(_window.Book.Display(0, 0), Is.EqualTo("42"));
			});
		}

		[Test]
		public void RectangularPasteFormattingAndWorksheetSwitchingWork()
		{
			_window.PasteText("2\t3\n=SUM(A1:B1)\tword");
			Assert.That(_window.Book.Display(1, 0), Is.EqualTo("5"));
			_window.SelectCell(0, 0); Click("BoldButton");
			Assert.That(_window.Book.Appearance(0, 0).Bold, Is.True);
			_window.Book.AddSheet("Other");
			var tabs = (ListBox)_window.FindName("SheetTabs");
			tabs.ItemsSource = _window.Book.SheetNames;
			tabs.SelectedIndex = 1;
			_window.PasteText("other");
			tabs.SelectedIndex = 0;
			Assert.That(_window.Book.Display(0, 0), Is.EqualTo("2"));
			Assert.That(Grid.Items.Count, Is.GreaterThanOrEqualTo(100));
		}

		[TestCase(false)]
		[TestCase(true)]
		public void XlsAndXlsxOpenEditSaveThroughTheWindow(bool legacy)
		{
			RunOnDispatcher(async () =>
			{
				string source;
				using (var book = SpreadsheetBook.New(legacy))
				{
					book.Edit([new(0, 0, "original")]);
					source = Path.Combine(_root, "source" + book.Extension); book.Save(source);
				}
				Assert.That(await _window.OpenWorkbookAsync(source), Is.True);
				Assert.That(_window.Book.Input(0, 0), Is.EqualTo("original"));
				((TextBox)_window.FindName("FormulaBox")).Text = "changed"; Click("ApplyFormulaButton");
				string target = Path.Combine(_root, "edited" + _window.Book.Extension);
				Assert.That(await _window.SaveWorkbookAsync(chosenPath: target), Is.True);
				Assert.That(_window.Book.IsDirty, Is.False);
				using var opened = SpreadsheetBook.Open(target);
				Assert.That(opened.Input(0, 0), Is.EqualTo("changed"));
			});
		}

		private static TextBox? FindTextBox(DependencyObject element)
		{
			if (element is TextBox input) return input;
			for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
				if (FindTextBox(VisualTreeHelper.GetChild(element, index)) is TextBox child) return child;
			return null;
		}
		private void RunOnDispatcher(Func<Task> action)
		{
			Task task = _window.Dispatcher.InvokeAsync(action).Task.Unwrap();
			var frame = new DispatcherFrame(); var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
			timeout.Tick += (_, _) => frame.Continue = false;
			_ = task.ContinueWith(_ => _window.Dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
			timeout.Start(); Dispatcher.PushFrame(frame); timeout.Stop();
			Assert.That(task.IsCompleted, Is.True, "Sheets UI operation did not finish within 15 seconds.");
			task.GetAwaiter().GetResult();
		}
	}
}