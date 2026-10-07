using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Software.Notepad
{
	public partial class MainWindow : Window
	{
		private readonly ObservableCollection<EditorDocument> documents = [];
		private readonly string[] arguments;
		private bool updatingSettings;
		private bool busy;
		private bool closingPending;
		private bool closingApproved;
		private int untitledNumber;
		private EditorDocument? zeroLengthMatchDocument;
		private int zeroLengthMatchOffset = -1;
		public EditorDocument? ActiveDocument => TabStrip?.SelectedItem as EditorDocument;

		public MainWindow() : this([]) { }

		public MainWindow(string[] arguments)
		{
			this.arguments = arguments.Select(Path.GetFullPath).ToArray();
			InitializeComponent();
			TabStrip.ItemsSource = documents;
			NewDocument();
		}

		public EditorDocument NewDocument(string text = "")
		{
			var document = new EditorDocument($"Untitled {++untitledNumber}", text) { Visibility = Visibility.Collapsed };
			AddDocument(document);
			return document;
		}

		private void AddDocument(EditorDocument document)
		{
			document.StateChanged += DocumentChanged;
			document.WordWrap = WrapMenu.IsChecked;
			document.Options.ShowSpaces = document.Options.ShowTabs = WhitespaceMenu.IsChecked;
			document.FontSize = FontSizeSlider.Value;
			documents.Add(document);
			EditorHost.Children.Add(document);
			TabStrip.SelectedItem = document;
			TabStrip.ScrollIntoView(document);
			document.Focus();
		}

		private async void Window_Loaded(object sender, RoutedEventArgs eventArgs)
		{
			foreach (string path in arguments)
				await OpenFileAsync(path);
		}

		private void New_Click(object sender, RoutedEventArgs eventArgs)
		{
			if (!busy)
				NewDocument();
		}

		private async void Open_Click(object sender, RoutedEventArgs eventArgs)
		{
			var dialog = new OpenFileDialog { Multiselect = true, Filter = "Text / structured files|*.txt;*.xml;*.json;*.xsd;*.xaml;*.config;*.cs;*.js;*.html;*.css|All files|*.*", Title = "Open files" };
			if (dialog.ShowDialog(this) == true)
			{
				foreach (string path in dialog.FileNames)
					await OpenFileAsync(path);
			}
		}

		public async Task<EditorDocument?> OpenFileAsync(string path)
		{
			if (busy)
				return null;
			SetBusy(true);
			try
			{
				string fullPath = Path.GetFullPath(path);
				var existing = documents.FirstOrDefault(document => string.Equals(document.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
				if (existing != null)
				{
					TabStrip.SelectedItem = existing;
					return existing;
				}
				var file = await Task.Run(() => NotepadFile.Read(fullPath));
				var document = new EditorDocument(Path.GetFileName(file.Path), file.Text, file.Encoding, file.Path, file.Hash) { Visibility = Visibility.Collapsed };
				var empty = documents.Count == 1 && documents[0].FilePath == null && documents[0].Text.Length == 0 && !documents[0].IsDirty ? documents[0] : null;
				AddDocument(document);
				if (empty != null)
					RemoveDocument(empty);
				StatusText.Text = $"Opened {file.Path}";
				return document;
			}
			catch (Exception exception)
			{
				ShowError(exception);
				return null;
			}
			finally { SetBusy(false); }
		}

		private void Tab_Changed(object sender, SelectionChangedEventArgs eventArgs)
		{
			foreach (var document in documents)
				document.Visibility = document == ActiveDocument ? Visibility.Visible : Visibility.Collapsed;
			if (StructureTree != null)
			{
				StructureTree.ItemsSource = null;
				QueryResults.Text = "";
				UpdateSettings();
			}
		}

		private void DocumentChanged(EditorDocument document)
		{
			if (document == ActiveDocument)
			{
				Title = document.Title + " - Notepad";
				StatusText.Text = $"Ln {document.TextArea.Caret.Line}, Col {document.TextArea.Caret.Column} | {document.Text.Length:N0} chars | {document.SyntaxLanguage} | {document.FileEncoding.WebName}";
			}
		}

		private void UpdateSettings()
		{
			if (ActiveDocument is not EditorDocument document)
				return;
			updatingSettings = true;
			LanguageBox.SelectedItem = LanguageBox.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (string)item.Content == document.SyntaxLanguage);
			EncodingBox.SelectedIndex = document.FileEncoding.CodePage switch
			{
				65001 => document.FileEncoding.GetPreamble().Length == 0 ? 1 : 2,
				1200 => 3,
				1201 => 4,
				_ => 0
			};
			QueryMode.SelectedIndex = document.SyntaxLanguage == "XML" ? 1 : 0;
			updatingSettings = false;
			DocumentChanged(document);
		}

		private async void Save_Click(object sender, RoutedEventArgs eventArgs)
		{
			if (ActiveDocument is EditorDocument document)
				await SaveDocumentAsync(document);
		}

		private async void SaveAs_Click(object sender, RoutedEventArgs eventArgs)
		{
			if (ActiveDocument is EditorDocument document)
				await SaveDocumentAsync(document, saveAs: true);
		}

		private async void SaveAll_Click(object sender, RoutedEventArgs eventArgs)
		{
			foreach (var document in documents.ToArray().Where(document => document.IsDirty))
			{
				if (!await SaveDocumentAsync(document))
					break;
			}
		}

		public async Task<bool> SaveDocumentAsync(EditorDocument document, bool saveAs = false, string? chosenPath = null)
		{
			if (busy)
				return false;
			string? path = chosenPath ?? document.FilePath;
			if (saveAs || path == null)
			{
				var dialog = new SaveFileDialog { Title = "Save document", FileName = document.FilePath == null ? "" : Path.GetFileName(document.FilePath), Filter = "All files|*.*", OverwritePrompt = true };
				if (dialog.ShowDialog(this) != true)
					return false;
				path = dialog.FileName;
			}
			SetBusy(true);
			try
			{
				string fullPath = Path.GetFullPath(path);
				var other = documents.FirstOrDefault(item => item != document && string.Equals(item.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
				if (other != null)
					throw new IOException("That file is already open in another tab. Close that tab or choose a different save destination.");
				if (File.Exists(fullPath) && string.Equals(fullPath, document.FilePath, StringComparison.OrdinalIgnoreCase) && document.DiskHash != null)
				{
					string diskHash = await Task.Run(() =>
					{
						using var stream = File.OpenRead(fullPath);
						return Convert.ToHexString(SHA256.HashData(stream));
					});
					if (diskHash != document.DiskHash && MessageBox.Show(this, "This file changed outside Notepad. Replace the external changes?", "File changed on disk",
						MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
						return false;
				}
				else if (chosenPath != null && File.Exists(fullPath) && MessageBox.Show(this, "Replace the existing destination file?", "Save document",
					MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
					return false;
				string text = document.Text;
				Encoding encoding = document.FileEncoding;
				string hash = await Task.Run(() => NotepadFile.Save(fullPath, text, encoding, overwrite: true));
				document.MarkSaved(fullPath, hash);
				StatusText.Text = $"Saved {fullPath}";
				return true;
			}
			catch (Exception exception) { ShowError(exception); return false; }
			finally { SetBusy(false); }
		}

		private async Task<bool> ConfirmCloseAsync(EditorDocument document)
		{
			if (!document.IsDirty)
				return true;
			TabStrip.SelectedItem = document;
			var answer = MessageBox.Show(this, $"Save changes to {document.Title.TrimStart('*', ' ')}?", "Unsaved document", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
			return answer == MessageBoxResult.No || (answer == MessageBoxResult.Yes && await SaveDocumentAsync(document));
		}

		private async void CloseTab_Click(object sender, RoutedEventArgs eventArgs)
		{
			eventArgs.Handled = true;
			if (sender is Button { Tag: EditorDocument document })
				await CloseDocumentAsync(document);
		}

		private async void CloseActive_Click(object sender, RoutedEventArgs eventArgs)
		{
			if (ActiveDocument is EditorDocument document)
				await CloseDocumentAsync(document);
		}

		private async Task CloseDocumentAsync(EditorDocument document)
		{
			if (busy || !await ConfirmCloseAsync(document))
				return;
			RemoveDocument(document);
			if (documents.Count == 0)
				NewDocument();
		}

		private void RemoveDocument(EditorDocument document)
		{
			int index = documents.IndexOf(document);
			bool selected = document == ActiveDocument;
			document.StateChanged -= DocumentChanged;
			EditorHost.Children.Remove(document);
			documents.Remove(document);
			document.Dispose();
			if (selected && documents.Count > 0)
				TabStrip.SelectedIndex = Math.Min(index, documents.Count - 1);
		}

		private async void Window_Closing(object? sender, CancelEventArgs eventArgs)
		{
			if (busy || closingPending) { eventArgs.Cancel = true; return; }
			if (closingApproved || documents.All(document => !document.IsDirty))
				return;
			eventArgs.Cancel = true;
			closingPending = true;
			try
			{
				foreach (var document in documents.ToArray())
				{
					if (!await ConfirmCloseAsync(document))
						return;
				}
				closingApproved = true;
			}
			finally { closingPending = false; }
			_ = Dispatcher.BeginInvoke(Close);
		}

		private void Window_Closed(object? sender, EventArgs eventArgs)
		{
			foreach (var document in documents)
			{
				document.StateChanged -= DocumentChanged;
				document.Dispose();
			}
		}

		private void Exit_Click(object sender, RoutedEventArgs eventArgs) => Close();
		private void Undo_Click(object sender, RoutedEventArgs eventArgs) => ActiveDocument?.Undo();
		private void Redo_Click(object sender, RoutedEventArgs eventArgs) => ActiveDocument?.Redo();
		private void Cut_Click(object sender, RoutedEventArgs eventArgs) => ActiveDocument?.Cut();
		private void Copy_Click(object sender, RoutedEventArgs eventArgs) => ActiveDocument?.Copy();
		private void Paste_Click(object sender, RoutedEventArgs eventArgs) => ActiveDocument?.Paste();
		private void SelectAll_Click(object sender, RoutedEventArgs eventArgs) => ActiveDocument?.SelectAll();

		private void ShowSearch_Click(object sender, RoutedEventArgs eventArgs)
		{
			SearchPanel.Visibility = Visibility.Visible;
			if (ActiveDocument is EditorDocument document && document.SelectionLength > 0)
				FindBox.Text = document.SelectedText;
			FindBox.Focus();
			FindBox.SelectAll();
		}

		private void HideSearch_Click(object sender, RoutedEventArgs eventArgs) { SearchPanel.Visibility = Visibility.Collapsed; ActiveDocument?.Focus(); }
		private void Find_KeyDown(object sender, KeyEventArgs eventArgs)
		{
			if (eventArgs.Key == Key.Enter) { eventArgs.Handled = true; FindNext_Click(sender, new RoutedEventArgs()); }
		}

		private void FindNext_Click(object sender, RoutedEventArgs eventArgs)
		{
			if (ActiveDocument is not EditorDocument document)
				return;
			try
			{
				int start = document.SelectionStart + document.SelectionLength;
				if (document.SelectionLength == 0 && zeroLengthMatchDocument == document && zeroLengthMatchOffset == start)
					start = start < document.Text.Length ? start + 1 : 0;
				var match = TextSearch.Find(document.Text, FindBox.Text, start, MatchCase.IsChecked == true, UseRegex.IsChecked == true, WholeWord.IsChecked == true);
				if (match.Success)
				{
					document.Select(match.Index, match.Length);
					zeroLengthMatchDocument = match.Length == 0 ? document : null;
					zeroLengthMatchOffset = match.Index;
					document.ScrollToLine(document.Document.GetLineByOffset(match.Index).LineNumber);
					StatusText.Text = $"Match at character {match.Index + 1}";
				}
				else
				{
					zeroLengthMatchDocument = null;
					StatusText.Text = "No match.";
				}
			}
			catch (Exception exception) { ShowError(exception); }
		}

		private void Replace_Click(object sender, RoutedEventArgs eventArgs)
		{
			if (ActiveDocument is not EditorDocument document)
				return;
			try
			{
				var pattern = TextSearch.Pattern(FindBox.Text, MatchCase.IsChecked == true, UseRegex.IsChecked == true, WholeWord.IsChecked == true);
				var match = pattern.Match(document.Text, document.SelectionStart);
				if (match.Success && match.Index == document.SelectionStart && match.Length == document.SelectionLength)
				{
					string replacement = UseRegex.IsChecked == true ? match.Result(ReplaceBox.Text) : ReplaceBox.Text;
					document.Document.Replace(match.Index, match.Length, replacement);
					document.Select(match.Index, replacement.Length);
				}
				FindNext_Click(sender, eventArgs);
			}
			catch (Exception exception) { ShowError(exception); }
		}

		private void ReplaceAll_Click(object sender, RoutedEventArgs eventArgs)
		{
			if (ActiveDocument is not EditorDocument document)
				return;
			try
			{
				var result = TextSearch.ReplaceAll(document.Text, FindBox.Text, ReplaceBox.Text, MatchCase.IsChecked == true, UseRegex.IsChecked == true, WholeWord.IsChecked == true);
				document.Document.Replace(0, document.Text.Length, result.Text);
				StatusText.Text = $"Replaced {result.Count} occurrence(s).";
			}
			catch (Exception exception) { ShowError(exception); }
		}

		private async void Tool_Click(object sender, RoutedEventArgs eventArgs)
		{
			if (sender is MenuItem { Tag: string tool })
				await RunToolAsync(tool);
		}

		private string ActiveStructuredLanguage => ActiveDocument?.SyntaxLanguage is "XML" or "JSON" ? ActiveDocument.SyntaxLanguage
			: EditorDocument.GuessLanguage(null, ActiveDocument?.Text ?? "");

		private async void Format_Click(object sender, RoutedEventArgs eventArgs) => await RunToolAsync(ActiveStructuredLanguage == "XML" ? "XmlFormat" : "JsonFormat");
		private async void Validate_Click(object sender, RoutedEventArgs eventArgs) => await RunToolAsync(ActiveStructuredLanguage == "XML" ? "XmlValidate" : "JsonValidate");
		private async void RefreshTree_Click(object sender, RoutedEventArgs eventArgs) => await RunToolAsync(ActiveStructuredLanguage == "XML" ? "XmlTree" : "JsonTree");

		public async Task<bool> RunToolAsync(string tool)
		{
			if (busy || ActiveDocument is not EditorDocument document)
				return false;
			bool xml = tool.StartsWith("Xml", StringComparison.Ordinal);
			if (tool.EndsWith("QueryMode", StringComparison.Ordinal))
			{
				ShowInspector(true);
				QueryMode.SelectedIndex = xml ? 1 : 0;
				QueryBox.Focus();
				return true;
			}
			string? schema = null;
			if (tool == "XmlSchema")
			{
				var dialog = new OpenFileDialog { Title = "Validate against XSD", Filter = "XML schema|*.xsd|All files|*.*" };
				if (dialog.ShowDialog(this) != true)
					return false;
				try { schema = NotepadFile.Read(dialog.FileName).Text; }
				catch (Exception exception) { ShowError(exception); return false; }
			}
			bool transform = tool.EndsWith("Format", StringComparison.Ordinal) || tool.EndsWith("Compact", StringComparison.Ordinal) || tool == "JsonSort";
			int offset = transform && document.SelectionLength > 0 ? document.SelectionStart : 0;
			int length = transform && document.SelectionLength > 0 ? document.SelectionLength : document.Text.Length;
			string text = document.Document.GetText(offset, length);
			SetBusy(true);
			StatusText.Text = "Processing...";
			try
			{
				var result = await Task.Run(() => tool switch
				{
					"XmlFormat" => (object)StructuredTextTools.FormatXml(text),
					"XmlCompact" => StructuredTextTools.FormatXml(text, compact: true),
					"JsonFormat" => StructuredTextTools.FormatJson(text),
					"JsonCompact" => StructuredTextTools.FormatJson(text, compact: true),
					"JsonSort" => StructuredTextTools.FormatJson(text, sortKeys: true),
					"XmlTree" => StructuredTextTools.XmlTree(text),
					"JsonTree" => StructuredTextTools.JsonTree(text),
					"XmlSchema" => StructuredTextTools.ValidateXmlSchema(text, schema!),
					"XmlValidate" => Validate(text, xml: true),
					"JsonValidate" => Validate(text, xml: false),
					_ => throw new ArgumentException("Unknown text tool.")
				});
				if (transform)
				{
					document.Document.Replace(offset, length, (string)result);
					document.SyntaxLanguage = xml ? "XML" : "JSON";
					document.Select(offset, ((string)result).Length);
					UpdateSettings();
					document.UpdateFoldings();
				}
				else if (result is StructureNode tree)
				{
					StructureTree.ItemsSource = new[] { tree };
					ShowInspector(true);
					InspectorTabs.SelectedIndex = 0;
				}
				else
				{
					QueryResults.Text = result is string[] errors ? errors.Length == 0 ? "Valid against the selected XSD." : string.Join(Environment.NewLine, errors) : (string)result;
					ShowInspector(true);
					InspectorTabs.SelectedIndex = 1;
				}
				StatusText.Text = tool + " completed.";
				return true;
			}
			catch (Exception exception) { ShowError(exception); return false; }
			finally { SetBusy(false); }

			static string Validate(string value, bool xml)
			{
				if (xml) StructuredTextTools.ValidateXml(value); else StructuredTextTools.ValidateJson(value);
				return xml ? "Valid XML." : "Valid JSON.";
			}
		}

		private async void Query_Click(object sender, RoutedEventArgs eventArgs) => await RunQueryAsync();
		private async void Query_KeyDown(object sender, KeyEventArgs eventArgs)
		{
			if (eventArgs.Key == Key.Enter) { eventArgs.Handled = true; await RunQueryAsync(); }
		}

		public async Task<bool> RunQueryAsync()
		{
			if (busy || ActiveDocument is not EditorDocument document)
				return false;
			string text = document.Text;
			string expression = QueryBox.Text;
			bool xml = QueryMode.SelectedIndex == 1;
			SetBusy(true);
			try
			{
				string result = await Task.Run(() => xml ? StructuredTextTools.QueryXml(text, expression) : StructuredTextTools.QueryJson(text, expression));
				QueryResults.Text = result.Length == 0 ? "No matches." : result;
				InspectorTabs.SelectedIndex = 1;
				StatusText.Text = "Query completed.";
				return true;
			}
			catch (Exception exception) { ShowError(exception); return false; }
			finally { SetBusy(false); }
		}

		private void Tree_Selected(object sender, RoutedPropertyChangedEventArgs<object> eventArgs)
		{
			if (eventArgs.NewValue is StructureNode node)
			{
				QueryBox.Text = node.Path;
				if (node.Line > 0 && ActiveDocument is EditorDocument document && node.Line <= document.Document.LineCount)
					document.ScrollTo(node.Line, Math.Max(1, node.Column));
			}
		}

		private void ShowError(Exception exception)
		{
			QueryResults.Text = exception.Message;
			InspectorTabs.SelectedIndex = 1;
			ShowInspector(true);
			StatusText.Text = "Operation failed; document unchanged.";
			if (exception is System.Xml.XmlException xml && xml.LineNumber > 0)
				ActiveDocument?.ScrollTo(xml.LineNumber, Math.Max(1, xml.LinePosition));
			else if (exception is System.Text.Json.JsonException json && json.LineNumber is long line)
				ActiveDocument?.ScrollTo(checked((int)line + 1), checked((int)(json.BytePositionInLine ?? 0) + 1));
		}

		private void SetBusy(bool value)
		{
			busy = value;
			MainMenu.IsEnabled = Toolbar.IsEnabled = Workspace.IsEnabled = SearchPanel.IsEnabled = SettingsBar.IsEnabled = !value;
		}

		private void Inspector_Click(object sender, RoutedEventArgs eventArgs) => ShowInspector(((MenuItem)sender).IsChecked);
		private void ShowInspector(bool visible)
		{
			Inspector.Visibility = InspectorSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
			InspectorColumn.Width = new GridLength(visible ? 330 : 0);
			InspectorDivider.Width = new GridLength(visible ? 7 : 0);
		}

		private void Language_Changed(object sender, SelectionChangedEventArgs eventArgs)
		{
			if (!updatingSettings && ActiveDocument is EditorDocument document && LanguageBox.SelectedItem is ComboBoxItem item)
			{
				document.SyntaxLanguage = (string)item.Content;
				QueryMode.SelectedIndex = document.SyntaxLanguage == "XML" ? 1 : 0;
			}
		}

		private void Encoding_Changed(object sender, SelectionChangedEventArgs eventArgs)
		{
			if (updatingSettings || ActiveDocument is not EditorDocument document)
				return;
			Encoding? encoding = EncodingBox.SelectedIndex switch
			{
				1 => new UTF8Encoding(false, true), 2 => new UTF8Encoding(true, true),
				3 => new UnicodeEncoding(false, true, true), 4 => new UnicodeEncoding(true, true, true), _ => null
			};
			if (encoding != null)
				document.SetEncoding(encoding);
		}

		private void Wrap_Click(object sender, RoutedEventArgs eventArgs)
		{
			foreach (var document in documents)
				document.WordWrap = WrapMenu.IsChecked;
		}

		private void Whitespace_Click(object sender, RoutedEventArgs eventArgs)
		{
			foreach (var document in documents)
				document.Options.ShowSpaces = document.Options.ShowTabs = WhitespaceMenu.IsChecked;
		}

		private void FontSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> eventArgs)
		{
			foreach (var document in documents)
				document.FontSize = eventArgs.NewValue;
		}

		private void LineEndings_Click(object sender, RoutedEventArgs eventArgs)
		{
			if (ActiveDocument is not EditorDocument document || sender is not MenuItem { Tag: string ending })
				return;
			string normalized = document.Text.Replace("\r\n", "\n").Replace('\r', '\n');
			string delimiter = ending == "CRLF" ? "\r\n" : ending == "CR" ? "\r" : "\n";
			document.Document.Replace(0, document.Text.Length, normalized.Replace("\n", delimiter));
		}

		private async void Window_PreviewKeyDown(object sender, KeyEventArgs eventArgs)
		{
			if (busy)
				return;
			var modifiers = Keyboard.Modifiers;
			if ((modifiers & ModifierKeys.Control) != 0 && (modifiers & ModifierKeys.Alt) == 0)
			{
				switch (eventArgs.Key)
				{
					case Key.N: eventArgs.Handled = true; NewDocument(); break;
					case Key.O: eventArgs.Handled = true; Open_Click(sender, new RoutedEventArgs()); break;
					case Key.S:
						eventArgs.Handled = true;
						if (ActiveDocument is EditorDocument document) await SaveDocumentAsync(document, (modifiers & ModifierKeys.Shift) != 0);
						break;
					case Key.W: eventArgs.Handled = true; CloseActive_Click(sender, new RoutedEventArgs()); break;
					case Key.F or Key.H: eventArgs.Handled = true; ShowSearch_Click(sender, new RoutedEventArgs()); break;
					case Key.Tab:
						eventArgs.Handled = true;
						TabStrip.SelectedIndex = (TabStrip.SelectedIndex + ((modifiers & ModifierKeys.Shift) != 0 ? -1 : 1) + documents.Count) % documents.Count;
						TabStrip.ScrollIntoView(TabStrip.SelectedItem);
						ActiveDocument?.Focus();
						break;
				}
			}
			else if (eventArgs.Key == Key.F3) { eventArgs.Handled = true; FindNext_Click(sender, new RoutedEventArgs()); }
			else if (eventArgs.Key == Key.Escape && SearchPanel.Visibility == Visibility.Visible) { eventArgs.Handled = true; HideSearch_Click(sender, new RoutedEventArgs()); }
		}

		private void Window_DragOver(object sender, DragEventArgs eventArgs)
		{
			eventArgs.Effects = !busy && eventArgs.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
			eventArgs.Handled = true;
		}

		private async void Window_Drop(object sender, DragEventArgs eventArgs)
		{
			if (!busy && eventArgs.Data.GetData(DataFormats.FileDrop) is string[] paths)
			{
				foreach (string path in paths)
					await OpenFileAsync(path);
			}
		}
	}
}