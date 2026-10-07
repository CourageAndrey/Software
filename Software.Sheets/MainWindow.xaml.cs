using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Software.Sheets
{
	public partial class MainWindow : Window
	{
		private readonly string[] _arguments;
		private SpreadsheetBook _book = SpreadsheetBook.New();
		private List<SheetRow> _rows = [];
		private string? _currentPath;
		private string? _diskHash;
		private bool _busy;
		private bool _updating;
		private bool _approvedClose;
		private bool _pendingClose;
		private bool _editingCell;
		private int _visibleRows;
		private int _visibleColumns;
		public SpreadsheetBook Book => _book;

		public MainWindow() : this([]) { }
		public MainWindow(string[] arguments)
		{
			if (arguments.Length > 1)
			{
				throw new ArgumentException("Use Sheets.exe [workbook path].");
			}

			this._arguments = arguments.Select(Path.GetFullPath).ToArray();
			InitializeComponent();
			ReloadSheets();
		}

		private async void Window_Loaded(object sender, RoutedEventArgs eventArgs) { if (_arguments.Length == 1)
			{
				await OpenWorkbookAsync(_arguments[0]);
			}
		}

		private void ReloadSheets()
		{
			_updating = true;
			SheetTabs.ItemsSource = _book.SheetNames;
			SheetTabs.SelectedIndex = _book.ActiveSheet;
			_updating = false;
			var dimensions = _book.Dimensions(); _visibleRows = dimensions.Rows; _visibleColumns = dimensions.Columns;
			BuildGrid(); UpdateState();
		}

		private void BuildGrid()
		{
			_updating = true;
			CellGrid.ItemsSource = null; CellGrid.Columns.Clear();
			_rows = Enumerable.Range(0, _visibleRows).Select(index => new SheetRow(_book, index)).ToList();
			for (int column = 0; column < _visibleColumns; column++)
			{
				string cellPath = $"[{column}]";
				var text = new FrameworkElementFactory(typeof(TextBlock));
				text.SetBinding(TextBlock.TextProperty, new Binding(cellPath + ".Display"));
				text.SetBinding(TextBlock.FontWeightProperty, new Binding(cellPath + ".Weight"));
				text.SetBinding(TextBlock.FontStyleProperty, new Binding(cellPath + ".Style"));
				text.SetBinding(TextBlock.ForegroundProperty, new Binding(cellPath + ".Foreground"));
				text.SetBinding(TextBlock.BackgroundProperty, new Binding(cellPath + ".Background"));
				text.SetBinding(TextBlock.TextAlignmentProperty, new Binding(cellPath + ".Alignment"));
				text.SetValue(TextBlock.PaddingProperty, new Thickness(5, 3, 5, 3));
				var edit = new FrameworkElementFactory(typeof(TextBox));
				edit.SetBinding(TextBox.TextProperty, new Binding(cellPath + ".Input") { Mode = BindingMode.OneWay });
				edit.SetValue(TextBox.PaddingProperty, new Thickness(4, 2, 4, 2));
				CellGrid.Columns.Add(new DataGridTemplateColumn
				{
					Header = SpreadsheetBook.Address(0, column)[..^1], Width = 105, MinWidth = 45,
					CellTemplate = new DataTemplate { VisualTree = text }, CellEditingTemplate = new DataTemplate { VisualTree = edit }
				});
			}
			CellGrid.ItemsSource = _rows;
			_updating = false;
			SelectCell(0, 0);
		}

		public void SelectCell(int row, int column)
		{
			if (row >= _visibleRows || column >= _visibleColumns)
			{
				_visibleRows = Math.Min(SpreadsheetBook.MaximumRows, Math.Max(_visibleRows, row + 26));
				_visibleColumns = Math.Min(SpreadsheetBook.MaximumColumns, Math.Max(_visibleColumns, column + 5));
				BuildGrid();
			}
			var cell = new DataGridCellInfo(_rows[row], CellGrid.Columns[column]);
			CellGrid.SelectedCells.Clear(); CellGrid.CurrentCell = cell; CellGrid.SelectedCells.Add(cell);
			CellGrid.ScrollIntoView(_rows[row], CellGrid.Columns[column]);
			UpdateFormulaBar();
		}

		private (int Row, int Column) CurrentAddress => CellGrid.CurrentCell.Item is SheetRow row && CellGrid.CurrentCell.Column != null
			? (row.Index, CellGrid.CurrentCell.Column.DisplayIndex) : (0, 0);

		private void UpdateFormulaBar()
		{
			if (_updating || FormulaBox == null)
			{
				return;
			}

			var (row, column) = CurrentAddress;
			AddressBox.Text = SpreadsheetBook.Address(row, column); FormulaBox.Text = _book.EditableInput(row, column);
		}
		private void CurrentCell_Changed(object? sender, EventArgs eventArgs) { if (!_editingCell)
			{
				UpdateFormulaBar();
			}
		}
		private void Row_Loading(object sender, DataGridRowEventArgs eventArgs) => eventArgs.Row.Header = ((SheetRow)eventArgs.Row.Item).Number;
		private void Cell_Preparing(object sender, DataGridPreparingCellForEditEventArgs eventArgs) { _editingCell = true; var input = FindChild<TextBox>(eventArgs.EditingElement); input?.SelectAll(); }

		private void Cell_EditEnding(object sender, DataGridCellEditEndingEventArgs eventArgs)
		{
			if (eventArgs.EditAction != DataGridEditAction.Commit) { _editingCell = false; return; }
			var input = FindChild<TextBox>(eventArgs.EditingElement);
			if (input == null)
			{
				return;
			}

			try
			{
				_book.Edit([new CellEdit(((SheetRow)eventArgs.Row.Item).Index, eventArgs.Column.DisplayIndex, input.Text)]);
				_editingCell = false;
				_ = Dispatcher.BeginInvoke(RefreshCells);
			}
			catch (Exception exception) { eventArgs.Cancel = true; ShowError(exception); }
		}

		private static T? FindChild<T>(DependencyObject element) where T : DependencyObject
		{
			if (element is T target)
			{
				return target;
			}

			for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
			{
				if (FindChild<T>(VisualTreeHelper.GetChild(element, index)) is T child)
				{
					return child;
				}
			}
			return null;
		}

		private bool CommitGrid() => CellGrid.CommitEdit(DataGridEditingUnit.Cell, true) && CellGrid.CommitEdit(DataGridEditingUnit.Row, true);
		private void RefreshCells() { foreach (var row in _rows) { row.Refresh(); } UpdateFormulaBar(); UpdateState(); }
		private void UpdateState() { Title = (_book.IsDirty ? "* " : "") + (_currentPath == null ? "Untitled" + _book.Extension : Path.GetFileName(_currentPath)) + " - Sheets"; UndoButton.IsEnabled = _book.CanUndo; RedoButton.IsEnabled = _book.CanRedo; }

		private void Formula_KeyDown(object sender, KeyEventArgs eventArgs)
		{
			if (eventArgs.Key == Key.Enter) { eventArgs.Handled = true; ApplyFormula_Click(sender, new RoutedEventArgs()); CellGrid.Focus(); }
			else if (eventArgs.Key == Key.Escape) { eventArgs.Handled = true; UpdateFormulaBar(); CellGrid.Focus(); }
		}
		private void ApplyFormula_Click(object sender, RoutedEventArgs eventArgs)
		{
			if (!CommitGrid())
			{
				return;
			}

			try { var address = CurrentAddress; _book.Edit([new CellEdit(address.Row, address.Column, FormulaBox.Text)]); RefreshCells(); }
			catch (Exception exception) { ShowError(exception); }
		}
		private void Address_KeyDown(object sender, KeyEventArgs eventArgs)
		{
			if (eventArgs.Key != Key.Enter)
			{
				return;
			}

			eventArgs.Handled = true;
			try { if (!CommitGrid()) { return; } var address = SpreadsheetBook.ParseAddress(AddressBox.Text); SelectCell(address.Row, address.Column); }
			catch (Exception exception) { ShowError(exception); }
		}

		private async void New_Click(object sender, RoutedEventArgs eventArgs) => await NewWorkbookAsync(false);
		private async void NewXls_Click(object sender, RoutedEventArgs eventArgs) => await NewWorkbookAsync(true);
		private async Task NewWorkbookAsync(bool legacy)
		{
			if (_busy || !CommitGrid() || !await ConfirmDiscardAsync())
			{
				return;
			}

			_book.Dispose(); _book = SpreadsheetBook.New(legacy); _currentPath = _diskHash = null; ReloadSheets();
		}
		private async void Open_Click(object sender, RoutedEventArgs eventArgs)
		{
			var dialog = new OpenFileDialog { Title = "Open workbook", Filter = "Excel workbooks|*.xls;*.xlsx|Excel 97-2003|*.xls|Excel workbook|*.xlsx" };
			if (dialog.ShowDialog(this) == true)
			{
				await OpenWorkbookAsync(dialog.FileName);
			}
		}
		public async Task<bool> OpenWorkbookAsync(string path)
		{
			if (_busy || !CommitGrid() || !await ConfirmDiscardAsync())
			{
				return false;
			}

			SetBusy(true); StatusText.Text = "Opening workbook...";
			try
			{
				var loaded = await Task.Run(() => SpreadsheetBook.Open(path));
				string hash;
				try { hash = HashFile(path); } catch { loaded.Dispose(); throw; }
				_book.Dispose(); _book = loaded; _currentPath = Path.GetFullPath(path); _diskHash = hash;
				ReloadSheets(); StatusText.Text = $"Opened {_currentPath}"; return true;
			}
			catch (Exception exception) { ShowError(exception); return false; }
			finally { SetBusy(false); }
		}
		private async void Save_Click(object sender, RoutedEventArgs eventArgs) => await SaveWorkbookAsync();
		private async void SaveAs_Click(object sender, RoutedEventArgs eventArgs) => await SaveWorkbookAsync(saveAs: true);
		public async Task<bool> SaveWorkbookAsync(bool saveAs = false, string? chosenPath = null)
		{
			if (_busy || !CommitGrid())
			{
				return false;
			}

			string? path = chosenPath ?? _currentPath;
			if (saveAs || path == null)
			{
				var dialog = new SaveFileDialog { Title = "Save workbook", Filter = $"Excel workbook|*{_book.Extension}", DefaultExt = _book.Extension, AddExtension = true, OverwritePrompt = true, FileName = _currentPath == null ? "" : Path.GetFileName(_currentPath) };
				if (dialog.ShowDialog(this) != true)
				{
					return false;
				}

				path = dialog.FileName;
			}
			SetBusy(true);
			try
			{
				if (File.Exists(path) && string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase) && _diskHash != null && HashFile(path) != _diskHash
					&& MessageBox.Show(this, "The workbook changed outside Sheets. Replace the external changes?", "Workbook changed", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
				{
					return false;
				}

				if (chosenPath != null && File.Exists(path) && !string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase)
					&& MessageBox.Show(this, "Replace the existing destination?", "Save workbook", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
				{
					return false;
				}

				await Task.Run(() => _book.Save(path, overwrite: true));
				_currentPath = Path.GetFullPath(path); _diskHash = HashFile(_currentPath); UpdateState(); StatusText.Text = $"Saved {_currentPath}"; return true;
			}
			catch (Exception exception) { ShowError(exception); return false; }
			finally { SetBusy(false); }
		}
		private async Task<bool> ConfirmDiscardAsync()
		{
			if (!_book.IsDirty)
			{
				return true;
			}

			var answer = MessageBox.Show(this, "Save changes to the current workbook?", "Unsaved workbook", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
			return answer == MessageBoxResult.No || (answer == MessageBoxResult.Yes && await SaveWorkbookAsync());
		}
		private async void Window_Closing(object? sender, CancelEventArgs eventArgs)
		{
			if (_busy || _pendingClose || !CommitGrid()) { eventArgs.Cancel = true; return; }
			if (_approvedClose || !_book.IsDirty)
			{
				return;
			}

			eventArgs.Cancel = true; _pendingClose = true;
			bool confirmed = await ConfirmDiscardAsync(); _pendingClose = false;
			if (confirmed) { _approvedClose = true; _ = Dispatcher.BeginInvoke(Close); }
		}
		private void Window_Closed(object? sender, EventArgs eventArgs) => _book.Dispose();
		private void Exit_Click(object sender, RoutedEventArgs eventArgs) => Close();
		private void Sheet_Changed(object sender, SelectionChangedEventArgs eventArgs)
		{
			if (_updating || SheetTabs.SelectedIndex < 0)
			{
				return;
			}

			if (!CommitGrid())
			{
				return;
			}

			_book.SelectSheet(SheetTabs.SelectedIndex); var dimensions = _book.Dimensions(); _visibleRows = dimensions.Rows; _visibleColumns = dimensions.Columns; BuildGrid();
		}
		private void AddSheet_Click(object sender, RoutedEventArgs eventArgs) => ManageSheet("Add");
		private void RenameSheet_Click(object sender, RoutedEventArgs eventArgs) => ManageSheet("Rename");
		private void DeleteSheet_Click(object sender, RoutedEventArgs eventArgs) => ManageSheet("Delete");
		private void ManageSheet(string action)
		{
			if (!CommitGrid())
			{
				return;
			}

			try
			{
				if (action == "Delete")
				{
					if (MessageBox.Show(this, $"Delete worksheet {_book.SheetNames[_book.ActiveSheet]} and all its cells?", "Delete worksheet", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
					{
						return;
					}

					_book.DeleteSheet();
				}
				else
				{
					string? name = PromptName(action + " worksheet", action == "Rename" ? _book.SheetNames[_book.ActiveSheet] : "Sheet" + (_book.SheetNames.Length + 1));
					if (name == null)
					{
						return;
					}

					if (action == "Add")
					{
						_book.AddSheet(name);
					}
					else
					{
						_book.RenameSheet(name);
					}
				}
				ReloadSheets(); StatusText.Text = "Worksheet changed. Cell undo history was reset.";
			}
			catch (Exception exception) { ShowError(exception); }
		}
		private string? PromptName(string title, string initial)
		{
			var input = new TextBox { Text = initial, Padding = new Thickness(5), Margin = new Thickness(0, 0, 0, 12) };
			var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 75 }; var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 75 };
			var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; buttons.Children.Add(ok); buttons.Children.Add(cancel);
			var content = new StackPanel { Margin = new Thickness(16) }; content.Children.Add(input); content.Children.Add(buttons);
			var dialog = new Window { Owner = this, Title = title, Content = content, Width = 360, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
			ok.Click += (_, _) => dialog.DialogResult = true; dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
			return dialog.ShowDialog() == true ? input.Text : null;
		}
		private void Undo_Click(object sender, RoutedEventArgs eventArgs) { if (!CommitGrid()) { return; } _book.Undo(); ReloadSheets(); }
		private void Redo_Click(object sender, RoutedEventArgs eventArgs) { if (!CommitGrid()) { return; } _book.Redo(); ReloadSheets(); }
		private (int Row, int Column)[] SelectedAddresses() => CellGrid.SelectedCells.Where(cell => cell.Item is SheetRow && cell.Column != null).Select(cell => (((SheetRow)cell.Item).Index, cell.Column.DisplayIndex)).Distinct().ToArray();
		private void Bold_Click(object sender, RoutedEventArgs eventArgs) => FormatSelection(bold: !_book.Appearance(CurrentAddress.Row, CurrentAddress.Column).Bold);
		private void Italic_Click(object sender, RoutedEventArgs eventArgs) => FormatSelection(italic: !_book.Appearance(CurrentAddress.Row, CurrentAddress.Column).Italic);
		private void Alignment_Click(object sender, RoutedEventArgs eventArgs) { if (sender is Button { Tag: string alignment })
			{
				FormatSelection(alignment: alignment);
			}
		}
		private void NumberFormat_Changed(object sender, SelectionChangedEventArgs eventArgs) { if (!_updating && CellGrid != null && NumberFormatBox.SelectedItem is ComboBoxItem { Tag: string format })
			{
				FormatSelection(numberFormat: format);
			}
		}
		private void FormatSelection(bool? bold = null, bool? italic = null, string? numberFormat = null, string? alignment = null)
		{
			if (!CommitGrid())
			{
				return;
			}

			try { _book.Format(SelectedAddresses(), bold, italic, numberFormat, alignment); RefreshCells(); }
			catch (Exception exception) { ShowError(exception); }
		}
		private void MoreRows_Click(object sender, RoutedEventArgs eventArgs) { if (!CommitGrid()) { return; } _visibleRows = Math.Min(SpreadsheetBook.MaximumRows, _visibleRows + 100); BuildGrid(); }
		private void MoreColumns_Click(object sender, RoutedEventArgs eventArgs) { if (!CommitGrid()) { return; } _visibleColumns = Math.Min(SpreadsheetBook.MaximumColumns, _visibleColumns + 10); BuildGrid(); }
		private void Copy_Click(object sender, RoutedEventArgs eventArgs)
		{
			if (!CommitGrid())
			{
				return;
			}

			var selected = SelectedAddresses(); if (selected.Length == 0)
			{
				return;
			}

			var chosen = selected.ToHashSet(); var text = new StringBuilder();
			for (int row = selected.Min(item => item.Row); row <= selected.Max(item => item.Row); row++)
			{
				if (row > selected.Min(item => item.Row))
				{
					text.AppendLine();
				}

				for (int column = selected.Min(item => item.Column); column <= selected.Max(item => item.Column); column++)
				{
					if (column > selected.Min(item => item.Column))
					{
						text.Append('\t');
					}

					if (chosen.Contains((row, column)))
					{
						text.Append(ClipboardCells.Encode(_book.EditableInput(row, column)));
					}
				}
			}
			try { Clipboard.SetText(text.ToString()); } catch (Exception exception) { ShowError(exception); }
		}
		private void Paste_Click(object sender, RoutedEventArgs eventArgs) { try { if (CommitGrid() && Clipboard.ContainsText()) { PasteText(Clipboard.GetText()); } } catch (Exception exception) { ShowError(exception); } }
		public void PasteText(string text)
		{
			var start = CurrentAddress;
			var edits = ClipboardCells.Parse(text, start.Row, start.Column);
			_book.Edit(edits); var dimensions = _book.Dimensions();
			if (dimensions.Rows > _visibleRows || dimensions.Columns > _visibleColumns) { _visibleRows = Math.Max(_visibleRows, dimensions.Rows); _visibleColumns = Math.Max(_visibleColumns, dimensions.Columns); BuildGrid(); SelectCell(start.Row, start.Column); }
			RefreshCells();
		}
		private void Clear_Click(object sender, RoutedEventArgs eventArgs) { if (!CommitGrid()) { return; } _book.Edit(SelectedAddresses().Select(item => new CellEdit(item.Row, item.Column, ""))); RefreshCells(); }
		private void ShowError(Exception exception) { StatusText.Text = exception.Message; MessageBox.Show(this, exception.Message, "Sheets", MessageBoxButton.OK, MessageBoxImage.Error); }
		private void SetBusy(bool value) { _busy = value; MainMenu.IsEnabled = Toolbar.IsEnabled = FormulaToolbar.IsEnabled = CellGrid.IsEnabled = SheetsToolbar.IsEnabled = !value; }
		private static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
		private async void Window_PreviewKeyDown(object sender, KeyEventArgs eventArgs)
		{
			if (_busy)
			{
				return;
			}

			bool textEditing = Keyboard.FocusedElement is TextBox;
			if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
			{
				switch (eventArgs.Key)
				{
					case Key.N: eventArgs.Handled = true; await NewWorkbookAsync(false); break;
					case Key.O: eventArgs.Handled = true; Open_Click(sender, new RoutedEventArgs()); break;
					case Key.S: eventArgs.Handled = true; await SaveWorkbookAsync((Keyboard.Modifiers & ModifierKeys.Shift) != 0); break;
					case Key.Z when !textEditing: eventArgs.Handled = true; Undo_Click(sender, new RoutedEventArgs()); break;
					case Key.Y when !textEditing: eventArgs.Handled = true; Redo_Click(sender, new RoutedEventArgs()); break;
					case Key.C when !textEditing: eventArgs.Handled = true; Copy_Click(sender, new RoutedEventArgs()); break;
					case Key.V when !textEditing: eventArgs.Handled = true; Paste_Click(sender, new RoutedEventArgs()); break;
				}
			}
			else if (eventArgs.Key == Key.Delete && !textEditing) { eventArgs.Handled = true; Clear_Click(sender, new RoutedEventArgs()); }
		}
		private void Window_DragOver(object sender, DragEventArgs eventArgs) { eventArgs.Effects = !_busy && eventArgs.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; eventArgs.Handled = true; }
		private async void Window_Drop(object sender, DragEventArgs eventArgs) { if (!_busy && eventArgs.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } paths)
			{
				await OpenWorkbookAsync(paths[0]);
			}
		}
	}
}