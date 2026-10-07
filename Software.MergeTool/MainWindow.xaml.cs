using System.Text;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using Microsoft.Win32;

namespace Software.MergeTool
{
	public partial class MainWindow : Window
	{
		private readonly string[] _arguments;
		private readonly DiffBackgroundRenderer _leftRenderer;
		private readonly DiffBackgroundRenderer _rightRenderer;
		private readonly SourceLineMargin _leftMargin;
		private readonly SourceLineMargin _rightMargin;
		private readonly List<AnchoredDifference> _differences = [];
		private TextFileDocument? _leftFile;
		private MergeComparison? _comparison;
		private string _savedText = "";
		private int _savedEncodingIndex;
		private string? _outputPath;
		private int _selectedDifference = -1;
		private bool _busy;
		private bool _loadingResult;
		private bool _synchronizingScroll;
		private bool _selectingDifference;

		private bool IsDirty => _leftFile != null && (ResultEditor.Text != _savedText || EncodingBox.SelectedIndex != _savedEncodingIndex);

		private sealed record AnchoredDifference(DifferenceBlock Block, TextAnchor Start, TextAnchor End)
		{
			public string ExpectedText { get; set; } = Block.LeftText;
		}

		public MainWindow() : this([]) { }

		public MainWindow(string[] arguments)
		{
			if (arguments.Length is not (0 or 2 or 3))
			{
				throw new ArgumentException("Use MergeTool.exe <left file> <right file> [output file].");
			}

			this._arguments = arguments.Select(Path.GetFullPath).ToArray();
			InitializeComponent();
			_leftRenderer = new DiffBackgroundRenderer(leftSide: true);
			_rightRenderer = new DiffBackgroundRenderer(leftSide: false);
			LeftEditor.TextArea.TextView.BackgroundRenderers.Add(_leftRenderer);
			RightEditor.TextArea.TextView.BackgroundRenderers.Add(_rightRenderer);
			_leftMargin = new SourceLineMargin(LeftEditor, leftSide: true);
			_rightMargin = new SourceLineMargin(RightEditor, leftSide: false);
			LeftEditor.TextArea.LeftMargins.Add(_leftMargin);
			RightEditor.TextArea.LeftMargins.Add(_rightMargin);
			LeftEditor.TextArea.TextView.ScrollOffsetChanged += (_, _) => SynchronizeScroll(LeftEditor, RightEditor);
			RightEditor.TextArea.TextView.ScrollOffsetChanged += (_, _) => SynchronizeScroll(RightEditor, LeftEditor);
			LeftEditor.TextArea.Caret.PositionChanged += (_, _) => SelectAtRow(LeftEditor.TextArea.Caret.Line - 1);
			RightEditor.TextArea.Caret.PositionChanged += (_, _) => SelectAtRow(RightEditor.TextArea.Caret.Line - 1);
		}

		private async void Window_Loaded(object sender, RoutedEventArgs eventArgs)
		{
			if (_arguments.Length >= 2)
			{
				if (_arguments.Length == 3)
				{
					_outputPath = Path.GetFullPath(_arguments[2]);
				}

				await LoadComparisonAsync(_arguments[0], _arguments[1]);
			}
		}

		private void BrowseLeft_Click(object sender, RoutedEventArgs eventArgs) => BrowseFile(LeftPathBox);
		private void BrowseRight_Click(object sender, RoutedEventArgs eventArgs) => BrowseFile(RightPathBox);

		private void BrowseFile(TextBox target)
		{
			var dialog = new OpenFileDialog { Title = "Choose text file", Filter = "All files|*.*" };
			if (dialog.ShowDialog(this) == true)
			{
				target.Text = dialog.FileName;
			}
		}

		private async void Compare_Click(object sender, RoutedEventArgs eventArgs) => await LoadComparisonAsync(LeftPathBox.Text, RightPathBox.Text);

		private async void Path_KeyDown(object sender, KeyEventArgs eventArgs)
		{
			if (eventArgs.Key == Key.Enter)
			{
				eventArgs.Handled = true;
				await LoadComparisonAsync(LeftPathBox.Text, RightPathBox.Text);
			}
		}

		private async void Swap_Click(object sender, RoutedEventArgs eventArgs)
		{
			await LoadComparisonAsync(RightPathBox.Text, LeftPathBox.Text);
		}

		public async Task<bool> LoadComparisonAsync(string leftPath, string rightPath)
		{
			if (_busy || !ConfirmDiscard())
			{
				return false;
			}

			SetBusy(true);
			StatusText.Text = "Comparing...";
			try
			{
				var data = await Task.Run(() =>
				{
					var left = TextFileDocument.Read(leftPath);
					var right = TextFileDocument.Read(rightPath);
					return (Left: left, Right: right, Comparison: MergeComparison.Compare(left.Text, right.Text));
				});
				_leftFile = data.Left;
				_comparison = data.Comparison;
				LeftPathBox.Text = data.Left.Path;
				RightPathBox.Text = data.Right.Path;
				LeftEditor.Text = _comparison.LeftDisplayText;
				RightEditor.Text = _comparison.RightDisplayText;
				_leftRenderer.Rows = _rightRenderer.Rows = _comparison.Rows;
				_leftMargin.Rows = _rightMargin.Rows = _comparison.Rows;
				_leftMargin.InvalidateVisual();
				_rightMargin.InvalidateVisual();
				_loadingResult = true;
				ResultEditor.Text = data.Left.Text;
				ResultEditor.Document.UndoStack.ClearAll();
				EncodingBox.SelectedIndex = 0;
				_savedEncodingIndex = 0;
				_savedText = data.Left.Text;
				_loadingResult = false;
				_differences.Clear();
				foreach (var block in _comparison.Differences)
				{
					var start = ResultEditor.Document.CreateAnchor(block.LeftOffset);
					start.MovementType = AnchorMovementType.BeforeInsertion;
					start.SurviveDeletion = true;
					var end = ResultEditor.Document.CreateAnchor(block.LeftOffset + block.LeftText.Length);
					end.MovementType = AnchorMovementType.AfterInsertion;
					end.SurviveDeletion = true;
					_differences.Add(new AnchoredDifference(block, start, end));
				}
				LeftEditor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinitionByExtension(Path.GetExtension(data.Left.Path));
				RightEditor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinitionByExtension(Path.GetExtension(data.Right.Path));
				ResultEditor.SyntaxHighlighting = LeftEditor.SyntaxHighlighting;
				ResultEditor.IsEnabled = SaveButton.IsEnabled = SaveAsButton.IsEnabled = true;
				_selectedDifference = -1;
				if (_differences.Count > 0)
				{
					SelectDifference(0);
				}
				else
				{
					DifferenceLabel.Text = "Files are identical";
					_leftRenderer.SelectedStart = _rightRenderer.SelectedStart = -1;
				}
				UpdateCommands();
				UpdateTitle();
				StatusText.Text = $"{_differences.Count} difference(s) | {data.Left.Encoding.WebName} | Result starts from left";
				return true;
			}
			catch (Exception exception)
			{
				StatusText.Text = "Comparison failed.";
				MessageBox.Show(this, exception.Message, "Cannot compare files", MessageBoxButton.OK, MessageBoxImage.Error);
				return false;
			}
			finally
			{
				SetBusy(false);
			}
		}

		private void SynchronizeScroll(TextEditor source, TextEditor target)
		{
			if (_synchronizingScroll)
			{
				return;
			}

			_synchronizingScroll = true;
			target.ScrollToVerticalOffset(source.VerticalOffset);
			_synchronizingScroll = false;
		}

		private void SelectAtRow(int row)
		{
			if (_selectingDifference)
			{
				return;
			}

			int index = _differences.FindIndex(item => row >= item.Block.StartRow && row < item.Block.StartRow + item.Block.RowCount);
			if (index >= 0 && index != _selectedDifference)
			{
				SelectDifference(index);
			}
		}

		private void SelectDifference(int index)
		{
			if (index < 0 || index >= _differences.Count)
			{
				return;
			}

			_selectingDifference = true;
			try
			{
				_selectedDifference = index;
				var selected = _differences[index];
				_leftRenderer.SelectedStart = _rightRenderer.SelectedStart = selected.Block.StartRow;
				_leftRenderer.SelectedCount = _rightRenderer.SelectedCount = selected.Block.RowCount;
				LeftEditor.ScrollToLine(selected.Block.StartRow + 1);
				RightEditor.ScrollToLine(selected.Block.StartRow + 1);
				LeftEditor.TextArea.Caret.Line = selected.Block.StartRow + 1;
				RightEditor.TextArea.Caret.Line = selected.Block.StartRow + 1;
				ResultEditor.Select(selected.Start.Offset, Math.Max(0, selected.End.Offset - selected.Start.Offset));
				ResultEditor.ScrollToLine(ResultEditor.Document.GetLineByOffset(selected.Start.Offset).LineNumber);
				LeftEditor.TextArea.TextView.InvalidateLayer(ICSharpCode.AvalonEdit.Rendering.KnownLayer.Background);
				RightEditor.TextArea.TextView.InvalidateLayer(ICSharpCode.AvalonEdit.Rendering.KnownLayer.Background);
				DifferenceLabel.Text = $"Difference {index + 1} of {_differences.Count}";
				UpdateCommands();
			}
			finally { _selectingDifference = false; }
		}

		private void UpdateCommands()
		{
			PreviousButton.IsEnabled = _selectedDifference > 0;
			NextButton.IsEnabled = _selectedDifference >= 0 && _selectedDifference < _differences.Count - 1;
			MergeCommands.IsEnabled = _selectedDifference >= 0;
		}

		private void Previous_Click(object sender, RoutedEventArgs eventArgs) => SelectDifference(_selectedDifference - 1);
		private void Next_Click(object sender, RoutedEventArgs eventArgs) => SelectDifference(_selectedDifference + 1);
		private void UseLeft_Click(object sender, RoutedEventArgs eventArgs) => ApplyChoice("Left");
		private void UseRight_Click(object sender, RoutedEventArgs eventArgs) => ApplyChoice("Right");
		private void UseBoth_Click(object sender, RoutedEventArgs eventArgs) => ApplyChoice("Both");

		private void ApplyChoice(string choice)
		{
			if (_busy || _selectedDifference < 0)
			{
				return;
			}

			var selected = _differences[_selectedDifference];
			int offset = selected.Start.Offset;
			int length = Math.Max(0, selected.End.Offset - offset);
			string current = ResultEditor.Document.GetText(offset, length);
			bool recognized = current == selected.ExpectedText || current == selected.Block.LeftText
				|| current == selected.Block.RightText || current == selected.Block.BothText;
			if (!recognized && MessageBox.Show(this, "This section contains manual edits. Replace them with the selected source?",
				"Replace manual edits", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
			{
				return;
			}

			string text = choice switch { "Left" => selected.Block.LeftText, "Right" => selected.Block.RightText, _ => selected.Block.BothText };
			ResultEditor.Document.Replace(offset, length, text);
			selected.ExpectedText = text;
			ResultEditor.Select(offset, text.Length);
			StatusText.Text = $"Applied {choice.ToLowerInvariant()} to difference {_selectedDifference + 1}.";
		}

		private void Undo_Click(object sender, RoutedEventArgs eventArgs) => ResultEditor.Undo();
		private void Redo_Click(object sender, RoutedEventArgs eventArgs) => ResultEditor.Redo();

		private void Result_TextChanged(object? sender, EventArgs eventArgs)
		{
			if (!_loadingResult)
			{
				UpdateTitle();
			}
		}

		private void Encoding_Changed(object sender, SelectionChangedEventArgs eventArgs)
		{
			if (!_loadingResult && ResultEditor != null)
			{
				UpdateTitle();
			}
		}

		private void UpdateTitle()
		{
			if (ResultLabel == null)
			{
				return;
			}

			Title = (IsDirty ? "* " : "") + "Merge Tool";
			ResultLabel.Text = (IsDirty ? "* " : "") + "Merge result" + (_outputPath == null ? "" : $" - {_outputPath}");
			ResultLabel.ToolTip = _outputPath;
		}

		private async void Save_Click(object sender, RoutedEventArgs eventArgs) => await SaveResultAsync(saveAs: _outputPath == null);
		private async void SaveAs_Click(object sender, RoutedEventArgs eventArgs) => await SaveResultAsync(saveAs: true);

		private async Task<bool> SaveResultAsync(bool saveAs)
		{
			if (_busy || _leftFile == null)
			{
				return false;
			}

			string? path = _outputPath;
			if (saveAs)
			{
				var dialog = new SaveFileDialog { Title = "Save merge result", FileName = Path.GetFileName(path ?? _leftFile.Path), Filter = "All files|*.*", OverwritePrompt = true };
				if (dialog.ShowDialog(this) != true)
				{
					return false;
				}

				path = dialog.FileName;
			}
			if (path == null)
			{
				return false;
			}

			if (!saveAs && File.Exists(path) && MessageBox.Show(this, $"Replace the existing result file?\n{path}", "Save result",
				MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
			{
				return false;
			}

			string text = ResultEditor.Text;
			int encodingIndex = EncodingBox.SelectedIndex;
			Encoding encoding = encodingIndex switch
			{
				1 => new UTF8Encoding(false, true),
				2 => new UTF8Encoding(true, true),
				3 => new UnicodeEncoding(false, true, true),
				_ => _leftFile.Encoding
			};
			SetBusy(true);
			try
			{
				await Task.Run(() => TextFileDocument.Save(path, text, encoding, overwrite: true));
				_outputPath = path;
				_savedText = text;
				_savedEncodingIndex = encodingIndex;
				UpdateTitle();
				StatusText.Text = $"Saved {path}";
				return true;
			}
			catch (Exception exception)
			{
				MessageBox.Show(this, exception.Message, "Cannot save result", MessageBoxButton.OK, MessageBoxImage.Error);
				return false;
			}
			finally { SetBusy(false); }
		}

		private void SetBusy(bool value)
		{
			_busy = value;
			Toolbar.IsEnabled = Workspace.IsEnabled = EncodingBox.IsEnabled = !value;
		}

		private bool ConfirmDiscard() => !IsDirty || MessageBox.Show(this, "Discard unsaved changes to the merge result?", "Unsaved result",
			MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

		private void Window_Closing(object? sender, CancelEventArgs eventArgs)
		{
			if (_busy)
			{
				eventArgs.Cancel = true;
				StatusText.Text = "Wait for the current operation to finish before closing.";
			}
			else
			{
				eventArgs.Cancel = !ConfirmDiscard();
			}
		}

		private async void Window_PreviewKeyDown(object sender, KeyEventArgs eventArgs)
		{
			if (_busy)
			{
				return;
			}

			if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && eventArgs.Key == Key.S)
			{
				eventArgs.Handled = true;
				await SaveResultAsync((Keyboard.Modifiers & ModifierKeys.Shift) != 0 || _outputPath == null);
			}
			else if (eventArgs.Key == Key.F7)
			{
				eventArgs.Handled = true;
				SelectDifference(_selectedDifference + ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1));
			}
		}
	}
}