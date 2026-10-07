using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Software.TextProcessor
{
	public partial class MainWindow : Window
	{
		private readonly string[] _arguments;
		private string? _currentPath;
		private string? _diskHash;
		private byte[] _savedDocument = [];
		private bool _loading = true;
		private bool _updatingFormat;
		private bool _busy;
		private bool _approvedClose;
		private bool _pendingClose;
		private int _searchOffset;
		public bool IsDirty => !SerializeDocument(Editor.Document).AsSpan().SequenceEqual(_savedDocument);

		public MainWindow() : this([]) { }

		public MainWindow(string[] arguments)
		{
			if (arguments.Length > 1)
			{
				throw new ArgumentException("Use TextProcessor.exe [document path].");
			}

			this._arguments = arguments.Select(Path.GetFullPath).ToArray();
			InitializeComponent();
			Editor.Document = DocumentFiles.NewDocument();
			Editor.Document.Blocks.Add(new Paragraph());
			FontBox.ItemsSource = Fonts.SystemFontFamilies.OrderBy(font => font.Source).ToArray();
			SizeBox.ItemsSource = new double[] { 8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 36, 48, 72 };
			_savedDocument = SerializeDocument(Editor.Document);
			_loading = false;
			UpdateTitle();
			UpdateFormatting();
		}

		private async void Window_Loaded(object sender, RoutedEventArgs eventArgs)
		{
			if (_arguments.Length > 0)
			{
				await OpenDocumentAsync(_arguments[0]);
			}

			Editor.Focus();
		}

		private void UpdateTitle() => Title = (IsDirty ? "* " : "") + (_currentPath == null ? "Untitled" : Path.GetFileName(_currentPath)) + " - Text Processor";

		private void Editor_TextChanged(object sender, TextChangedEventArgs eventArgs)
		{
			if (!_loading && Editor != null) { _searchOffset = 0; UpdateTitle(); StatusText.Text = IsDirty ? "Modified" : "Ready"; }
		}

		private void Editor_SelectionChanged(object sender, RoutedEventArgs eventArgs)
		{
			if (!_loading && FontBox != null)
			{
				UpdateFormatting();
			}
		}

		private void UpdateFormatting()
		{
			_updatingFormat = true;
			var family = Editor.Selection.GetPropertyValue(TextElement.FontFamilyProperty);
			FontBox.SelectedItem = family is FontFamily font ? FontBox.Items.Cast<FontFamily>().FirstOrDefault(item => item.Source == font.Source) : null;
			var size = Editor.Selection.GetPropertyValue(TextElement.FontSizeProperty);
			SizeBox.Text = size is double pixels ? (pixels * 0.75).ToString("0.#", System.Globalization.CultureInfo.CurrentCulture) : "";
			BoldButton.IsChecked = Editor.Selection.GetPropertyValue(TextElement.FontWeightProperty) is FontWeight weight && weight.ToOpenTypeWeight() >= 600;
			ItalicButton.IsChecked = Equals(Editor.Selection.GetPropertyValue(TextElement.FontStyleProperty), FontStyles.Italic);
			var decorations = Editor.Selection.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
			UnderlineButton.IsChecked = decorations?.Any(item => item.Location == TextDecorationLocation.Underline) == true;
			StrikeButton.IsChecked = decorations?.Any(item => item.Location == TextDecorationLocation.Strikethrough) == true;
			_updatingFormat = false;
		}

		private void Font_Changed(object sender, SelectionChangedEventArgs eventArgs)
		{
			if (!_loading && !_updatingFormat && FontBox.SelectedItem is FontFamily font)
			{
				Editor.Selection.ApplyPropertyValue(TextElement.FontFamilyProperty, font);
			}
		}

		private void Size_Changed(object sender, SelectionChangedEventArgs eventArgs)
		{
			if (!_loading && !_updatingFormat && SizeBox.SelectedItem is double points)
			{
				ApplyFontSize(points);
			}
		}

		private void Size_KeyDown(object sender, KeyEventArgs eventArgs)
		{
			if (eventArgs.Key != Key.Enter)
			{
				return;
			}

			eventArgs.Handled = true;
			if (double.TryParse(SizeBox.Text, out double points))
			{
				ApplyFontSize(points);
			}

			Editor.Focus();
		}

		private void ApplyFontSize(double points) { if (points is >= 4 and <= 200)
			{
				Editor.Selection.ApplyPropertyValue(TextElement.FontSizeProperty, points * 4 / 3);
			}
		}
		private void Bold_Click(object sender, RoutedEventArgs eventArgs) => EditingCommands.ToggleBold.Execute(null, Editor);
		private void Italic_Click(object sender, RoutedEventArgs eventArgs) => EditingCommands.ToggleItalic.Execute(null, Editor);
		private void Underline_Click(object sender, RoutedEventArgs eventArgs) => EditingCommands.ToggleUnderline.Execute(null, Editor);

		private void Strike_Click(object sender, RoutedEventArgs eventArgs)
		{
			var next = (Editor.Selection.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection)?.Clone() ?? new TextDecorationCollection();
			bool strike = next.Any(item => item.Location == TextDecorationLocation.Strikethrough);
			foreach (var item in next.Where(item => item.Location == TextDecorationLocation.Strikethrough).ToArray())
			{
				next.Remove(item);
			}

			if (!strike)
			{
				next.Add(TextDecorations.Strikethrough[0]);
			}

			Editor.Selection.ApplyPropertyValue(Inline.TextDecorationsProperty, next);
		}

		private void Color_Changed(object sender, SelectionChangedEventArgs eventArgs)
		{
			if (!_loading && !_updatingFormat && ColorBox.SelectedItem is ComboBoxItem { Tag: string color })
			{
				Editor.Selection.ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)));
			}
		}

		private async void New_Click(object sender, RoutedEventArgs eventArgs)
		{
			if (_busy || !await ConfirmDiscardAsync())
			{
				return;
			}

			_loading = true;
			Editor.Document = DocumentFiles.NewDocument();
			Editor.Document.Blocks.Add(new Paragraph());
			Editor.IsUndoEnabled = false;
			Editor.IsUndoEnabled = true;
			_currentPath = _diskHash = null;
			_savedDocument = SerializeDocument(Editor.Document);
			ImportNote.Visibility = Visibility.Collapsed;
			_loading = false;
			UpdateTitle();
			Editor.Focus();
		}

		private async void Open_Click(object sender, RoutedEventArgs eventArgs)
		{
			var dialog = new OpenFileDialog { Title = "Open document", Filter = "Documents|*.rtf;*.txt;*.doc;*.docx|Rich text|*.rtf|Word documents|*.doc;*.docx|Text|*.txt" };
			if (dialog.ShowDialog(this) == true)
			{
				await OpenDocumentAsync(dialog.FileName);
			}
		}

		public async Task<bool> OpenDocumentAsync(string path)
		{
			if (_busy || !await ConfirmDiscardAsync())
			{
				return false;
			}

			SetBusy(true);
			StatusText.Text = "Opening document...";
			await Dispatcher.Yield(DispatcherPriority.Background);
			try
			{
				var loaded = DocumentFiles.Open(path);
				string hash = HashFile(path);
				_loading = true;
				Editor.Document = loaded.Document;
				Editor.IsUndoEnabled = false;
				Editor.IsUndoEnabled = true;
				_currentPath = Path.GetFullPath(path);
				_diskHash = hash;
				_savedDocument = SerializeDocument(Editor.Document);
				ImportNote.Text = loaded.ImportNote ?? "";
				ImportNote.Visibility = loaded.ImportNote == null ? Visibility.Collapsed : Visibility.Visible;
				_loading = false;
				UpdateTitle(); UpdateFormatting();
				StatusText.Text = $"Opened {_currentPath}";
				return true;
			}
			catch (Exception exception) { ShowError(exception); return false; }
			finally { _loading = false; SetBusy(false); }
		}

		private async void Save_Click(object sender, RoutedEventArgs eventArgs) => await SaveDocumentAsync();
		private async void SaveAs_Click(object sender, RoutedEventArgs eventArgs) => await SaveDocumentAsync(saveAs: true);

		public async Task<bool> SaveDocumentAsync(bool saveAs = false, string? chosenPath = null)
		{
			if (_busy)
			{
				return false;
			}

			string? path = chosenPath ?? _currentPath;
			if (path == null || saveAs || Path.GetExtension(path).Equals(".doc", StringComparison.OrdinalIgnoreCase))
			{
				var dialog = new SaveFileDialog { Title = "Save document", Filter = "Rich text|*.rtf|Word document|*.docx|Plain text|*.txt", DefaultExt = ".rtf", AddExtension = true,
					FileName = _currentPath == null ? "" : Path.GetFileNameWithoutExtension(_currentPath), OverwritePrompt = true };
				if (dialog.ShowDialog(this) != true)
				{
					return false;
				}

				path = dialog.FileName;
			}
			if (Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase) && MessageBox.Show(this,
				"Plain text does not retain formatting, images, or tables. Save as plain text?", "Formatting will be lost", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
			{
				return false;
			}

			SetBusy(true);
			await Dispatcher.Yield(DispatcherPriority.Background);
			try
			{
				if (File.Exists(path) && string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase) && _diskHash != null && HashFile(path) != _diskHash
					&& MessageBox.Show(this, "The file changed outside Text Processor. Replace the external changes?", "File changed", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
				{
					return false;
				}

				if (chosenPath != null && File.Exists(path) && !string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase)
					&& MessageBox.Show(this, "Replace the existing destination?", "Save document", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
				{
					return false;
				}

				DocumentFiles.Save(path, Editor.Document, overwrite: true);
				_currentPath = Path.GetFullPath(path);
				_diskHash = HashFile(_currentPath);
				_savedDocument = SerializeDocument(Editor.Document);
				UpdateTitle(); StatusText.Text = $"Saved {_currentPath}";
				return true;
			}
			catch (Exception exception) { ShowError(exception); return false; }
			finally { SetBusy(false); }
		}

		private async Task<bool> ConfirmDiscardAsync()
		{
			if (!IsDirty)
			{
				return true;
			}

			var answer = MessageBox.Show(this, "Save changes to the current document?", "Unsaved document", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
			return answer == MessageBoxResult.No || (answer == MessageBoxResult.Yes && await SaveDocumentAsync());
		}

		private async void Window_Closing(object? sender, CancelEventArgs eventArgs)
		{
			if (_busy || _pendingClose) { eventArgs.Cancel = true; return; }
			if (_approvedClose || !IsDirty)
			{
				return;
			}

			eventArgs.Cancel = true;
			_pendingClose = true;
			bool confirmed = await ConfirmDiscardAsync();
			_pendingClose = false;
			if (confirmed) { _approvedClose = true; _ = Dispatcher.BeginInvoke(Close); }
		}

		private void Exit_Click(object sender, RoutedEventArgs eventArgs) => Close();
		private void Undo_Click(object sender, RoutedEventArgs eventArgs) => Editor.Undo();
		private void Redo_Click(object sender, RoutedEventArgs eventArgs) => Editor.Redo();

		private void Print_Click(object sender, RoutedEventArgs eventArgs)
		{
			try
			{
				var dialog = new PrintDialog();
				if (dialog.ShowDialog() != true)
				{
					return;
				}

				var clone = DocumentFiles.NewDocument();
				using var stream = new MemoryStream();
				new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd).Save(stream, DataFormats.XamlPackage);
				stream.Position = 0;
				new TextRange(clone.ContentStart, clone.ContentEnd).Load(stream, DataFormats.XamlPackage);
				clone.PageWidth = dialog.PrintableAreaWidth; clone.PageHeight = dialog.PrintableAreaHeight;
				dialog.PrintDocument(((IDocumentPaginatorSource)clone).DocumentPaginator, _currentPath == null ? "Text Processor document" : Path.GetFileName(_currentPath));
			}
			catch (Exception exception) { ShowError(exception); }
		}

		private void Image_Click(object sender, RoutedEventArgs eventArgs)
		{
			var dialog = new OpenFileDialog { Title = "Insert image", Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff" };
			if (dialog.ShowDialog(this) != true)
			{
				return;
			}

			try
			{
				if (new FileInfo(dialog.FileName).Length > DocumentFiles.MaximumFileBytes)
				{
					throw new IOException("Choose an image of 20 MiB or less.");
				}

				var image = new BitmapImage();
				using (var stream = File.OpenRead(dialog.FileName)) { image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 1200; image.StreamSource = stream; image.EndInit(); }
				image.Freeze();
				Editor.Selection.Text = "";
				_ = new InlineUIContainer(new Image { Source = image, Width = Math.Min(image.Width, 600), Stretch = Stretch.Uniform }, Editor.CaretPosition);
				Editor.Focus();
			}
			catch (Exception exception) { ShowError(exception); }
		}

		private void Table_Click(object sender, RoutedEventArgs eventArgs)
		{
			var rows = new TextBox { Text = "3", Width = 65, Margin = new Thickness(8) };
			var columns = new TextBox { Text = "3", Width = 65, Margin = new Thickness(8) };
			var content = new StackPanel { Margin = new Thickness(18) };
			content.Children.Add(new TextBlock { Text = "Rows (1-20)" }); content.Children.Add(rows);
			content.Children.Add(new TextBlock { Text = "Columns (1-10)" }); content.Children.Add(columns);
			var ok = new Button { Content = "Insert", IsDefault = true, Margin = new Thickness(0, 10, 0, 0) };
			var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 6, 0, 0) };
			content.Children.Add(ok); content.Children.Add(cancel);
			var dialog = new Window { Title = "Insert table", Owner = this, Content = content, Width = 280, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
			ok.Click += (_, _) => { if (int.TryParse(rows.Text, out int rowCount) && rowCount is >= 1 and <= 20 && int.TryParse(columns.Text, out int columnCount) && columnCount is >= 1 and <= 10) { dialog.DialogResult = true; } };
			if (dialog.ShowDialog() == true)
			{
				InsertTable(int.Parse(rows.Text), int.Parse(columns.Text));
			}
		}

		public void InsertTable(int rows, int columns)
		{
			if (rows is < 1 or > 20 || columns is < 1 or > 10)
			{
				throw new ArgumentOutOfRangeException(nameof(rows));
			}

			var table = new Table { CellSpacing = 0 };
			var group = new TableRowGroup();
			for (int row = 0; row < rows; row++)
			{
				var target = new TableRow();
				for (int column = 0; column < columns; column++)
				{
					target.Cells.Add(new TableCell(new Paragraph(new Run(""))) { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Padding = new Thickness(5) });
				}

				group.Rows.Add(target);
			}
			table.RowGroups.Add(group);
			var paragraph = Editor.CaretPosition.Paragraph;
			if (paragraph?.Parent == Editor.Document)
			{
				Editor.Document.Blocks.InsertAfter(paragraph, table);
			}
			else
			{
				Editor.Document.Blocks.Add(table);
			}

			Editor.Focus();
		}

		private void Date_Click(object sender, RoutedEventArgs eventArgs) => Editor.Selection.Text = DateTime.Now.ToString("g");
		private void ShowSearch_Click(object sender, RoutedEventArgs eventArgs)
		{
			SearchPanel.Visibility = Visibility.Visible;
			if (!Editor.Selection.IsEmpty)
			{
				FindBox.Text = Editor.Selection.Text;
			}

			_searchOffset = 0; FindBox.Focus(); FindBox.SelectAll();
		}
		private void HideSearch_Click(object sender, RoutedEventArgs eventArgs) { SearchPanel.Visibility = Visibility.Collapsed; Editor.Focus(); }
		private void Find_KeyDown(object sender, KeyEventArgs eventArgs) { if (eventArgs.Key == Key.Enter) { eventArgs.Handled = true; FindNext_Click(sender, new RoutedEventArgs()); } }

		private List<TextRange> FindMatches()
		{
			var result = new List<TextRange>();
			if (FindBox.Text.Length == 0)
			{
				return result;
			}

			foreach (var paragraph in Paragraphs(Editor.Document.Blocks))
			{
				var pointers = new List<TextPointer>();
				var text = new StringBuilder();
				var pointer = paragraph.ContentStart;
				while (pointer != null && pointer.CompareTo(paragraph.ContentEnd) < 0)
				{
					if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
					{
						string run = pointer.GetTextInRun(LogicalDirection.Forward);
						for (int index = 0; index < run.Length; index++) { pointers.Add(pointer.GetPositionAtOffset(index)!); text.Append(run[index]); }
						pointer = pointer.GetPositionAtOffset(run.Length);
					}
					else
					{
						pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
					}
				}
				string value = text.ToString();
				int offset = 0;
				while (offset <= value.Length - FindBox.Text.Length)
				{
					int match = value.IndexOf(FindBox.Text, offset, MatchCase.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
					if (match < 0)
					{
						break;
					}

					result.Add(new TextRange(pointers[match], pointers[match + FindBox.Text.Length - 1].GetPositionAtOffset(1)!));
					offset = match + FindBox.Text.Length;
				}
			}
			return result;
		}

		private static IEnumerable<Paragraph> Paragraphs(BlockCollection blocks)
		{
			foreach (Block block in blocks)
			{
				if (block is Paragraph paragraph)
				{
					yield return paragraph;
				}
				else if (block is Section section) { foreach (var child in Paragraphs(section.Blocks))
					{
						yield return child;
					}
				}
				else if (block is System.Windows.Documents.List list) { foreach (var item in list.ListItems)
					{
						foreach (var child in Paragraphs(item.Blocks))
						{
							yield return child;
						}
					}
				}
				else if (block is Table table) { foreach (var group in table.RowGroups)
					{
						foreach (var row in group.Rows)
						{
							foreach (var cell in row.Cells)
							{
								foreach (var child in Paragraphs(cell.Blocks))
								{
									yield return child;
								}
							}
						}
					}
				}
			}
		}

		private void FindNext_Click(object sender, RoutedEventArgs eventArgs)
		{
			var matches = FindMatches();
			if (matches.Count == 0) { StatusText.Text = "No matches."; return; }
			var match = matches[_searchOffset % matches.Count];
			Editor.Selection.Select(match.Start, match.End);
			match.Start.Paragraph?.BringIntoView();
			_searchOffset++; StatusText.Text = $"Match {(_searchOffset - 1) % matches.Count + 1} of {matches.Count}";
		}
		private void Replace_Click(object sender, RoutedEventArgs eventArgs)
		{
			if (FindBox.Text.Length > 0 && Editor.Selection.Text.Equals(FindBox.Text, MatchCase.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
			{
				Editor.Selection.Text = ReplaceBox.Text;
			}

			FindNext_Click(sender, eventArgs);
		}
		private void ReplaceAll_Click(object sender, RoutedEventArgs eventArgs)
		{
			var matches = FindMatches();
			Editor.BeginChange();
			try { for (int index = matches.Count - 1; index >= 0; index--)
				{
					matches[index].Text = ReplaceBox.Text;
				}
			}
			finally { Editor.EndChange(); }
			StatusText.Text = $"Replaced {matches.Count} occurrence(s).";
		}

		private void Zoom_Changed(object sender, RoutedPropertyChangedEventArgs<double> eventArgs)
		{
			if (EditorScale == null)
			{
				return;
			}

			EditorScale.ScaleX = EditorScale.ScaleY = eventArgs.NewValue / 100; ZoomLabel.Text = $"{eventArgs.NewValue:0}%";
		}
		private void SetBusy(bool value) { _busy = value; MainMenu.IsEnabled = FileToolbar.IsEnabled = FormatToolbar.IsEnabled = Editor.IsEnabled = SearchPanel.IsEnabled = !value; }
		private void ShowError(Exception exception) { StatusText.Text = "Operation failed."; MessageBox.Show(this, exception.Message, "Text Processor", MessageBoxButton.OK, MessageBoxImage.Error); }
		private static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
		private static byte[] SerializeDocument(FlowDocument document) { using var stream = new MemoryStream(); new TextRange(document.ContentStart, document.ContentEnd).Save(stream, DataFormats.Rtf); return stream.ToArray(); }

		private async void Window_PreviewKeyDown(object sender, KeyEventArgs eventArgs)
		{
			if (_busy)
			{
				return;
			}

			if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
			{
				switch (eventArgs.Key)
				{
					case Key.N: eventArgs.Handled = true; New_Click(sender, new RoutedEventArgs()); break;
					case Key.O: eventArgs.Handled = true; Open_Click(sender, new RoutedEventArgs()); break;
					case Key.S: eventArgs.Handled = true; await SaveDocumentAsync((Keyboard.Modifiers & ModifierKeys.Shift) != 0); break;
					case Key.P: eventArgs.Handled = true; Print_Click(sender, new RoutedEventArgs()); break;
					case Key.F or Key.H: eventArgs.Handled = true; ShowSearch_Click(sender, new RoutedEventArgs()); break;
				}
			}
			else if (eventArgs.Key == Key.F3) { eventArgs.Handled = true; FindNext_Click(sender, new RoutedEventArgs()); }
			else if (eventArgs.Key == Key.Escape && SearchPanel.Visibility == Visibility.Visible) { eventArgs.Handled = true; HideSearch_Click(sender, new RoutedEventArgs()); }
		}
		private void Window_DragOver(object sender, DragEventArgs eventArgs) { eventArgs.Effects = !_busy && eventArgs.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; eventArgs.Handled = true; }
		private async void Window_Drop(object sender, DragEventArgs eventArgs) { if (!_busy && eventArgs.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } paths)
			{
				await OpenDocumentAsync(paths[0]);
			}
		}
	}
}