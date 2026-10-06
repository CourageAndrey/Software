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
        private readonly string[] arguments;
        private readonly DiffBackgroundRenderer leftRenderer;
        private readonly DiffBackgroundRenderer rightRenderer;
        private readonly SourceLineMargin leftMargin;
        private readonly SourceLineMargin rightMargin;
        private readonly List<AnchoredDifference> differences = [];
        private TextFileDocument? leftFile;
        private MergeComparison? comparison;
        private string savedText = "";
        private int savedEncodingIndex;
        private string? outputPath;
        private int selectedDifference = -1;
        private bool busy;
        private bool loadingResult;
        private bool synchronizingScroll;
        private bool selectingDifference;

        private bool IsDirty => leftFile != null && (ResultEditor.Text != savedText || EncodingBox.SelectedIndex != savedEncodingIndex);

        private sealed record AnchoredDifference(DifferenceBlock Block, TextAnchor Start, TextAnchor End)
        {
            public string ExpectedText { get; set; } = Block.LeftText;
        }

        public MainWindow() : this([]) { }

        public MainWindow(string[] arguments)
        {
            if (arguments.Length is not (0 or 2 or 3))
                throw new ArgumentException("Use MergeTool.exe <left file> <right file> [output file].");
            this.arguments = arguments.Select(Path.GetFullPath).ToArray();
            InitializeComponent();
            leftRenderer = new DiffBackgroundRenderer(leftSide: true);
            rightRenderer = new DiffBackgroundRenderer(leftSide: false);
            LeftEditor.TextArea.TextView.BackgroundRenderers.Add(leftRenderer);
            RightEditor.TextArea.TextView.BackgroundRenderers.Add(rightRenderer);
            leftMargin = new SourceLineMargin(LeftEditor, leftSide: true);
            rightMargin = new SourceLineMargin(RightEditor, leftSide: false);
            LeftEditor.TextArea.LeftMargins.Add(leftMargin);
            RightEditor.TextArea.LeftMargins.Add(rightMargin);
            LeftEditor.TextArea.TextView.ScrollOffsetChanged += (_, _) => SynchronizeScroll(LeftEditor, RightEditor);
            RightEditor.TextArea.TextView.ScrollOffsetChanged += (_, _) => SynchronizeScroll(RightEditor, LeftEditor);
            LeftEditor.TextArea.Caret.PositionChanged += (_, _) => SelectAtRow(LeftEditor.TextArea.Caret.Line - 1);
            RightEditor.TextArea.Caret.PositionChanged += (_, _) => SelectAtRow(RightEditor.TextArea.Caret.Line - 1);
        }

        private async void Window_Loaded(object sender, RoutedEventArgs eventArgs)
        {
            if (arguments.Length >= 2)
            {
                if (arguments.Length == 3)
                    outputPath = Path.GetFullPath(arguments[2]);
                await LoadComparisonAsync(arguments[0], arguments[1]);
            }
        }

        private void BrowseLeft_Click(object sender, RoutedEventArgs eventArgs) => BrowseFile(LeftPathBox);
        private void BrowseRight_Click(object sender, RoutedEventArgs eventArgs) => BrowseFile(RightPathBox);

        private void BrowseFile(TextBox target)
        {
            var dialog = new OpenFileDialog { Title = "Choose text file", Filter = "All files|*.*" };
            if (dialog.ShowDialog(this) == true)
                target.Text = dialog.FileName;
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
            if (busy || !ConfirmDiscard())
                return false;
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
                leftFile = data.Left;
                comparison = data.Comparison;
                LeftPathBox.Text = data.Left.Path;
                RightPathBox.Text = data.Right.Path;
                LeftEditor.Text = comparison.LeftDisplayText;
                RightEditor.Text = comparison.RightDisplayText;
                leftRenderer.Rows = rightRenderer.Rows = comparison.Rows;
                leftMargin.Rows = rightMargin.Rows = comparison.Rows;
                leftMargin.InvalidateVisual();
                rightMargin.InvalidateVisual();
                loadingResult = true;
                ResultEditor.Text = data.Left.Text;
                ResultEditor.Document.UndoStack.ClearAll();
                EncodingBox.SelectedIndex = 0;
                savedEncodingIndex = 0;
                savedText = data.Left.Text;
                loadingResult = false;
                differences.Clear();
                foreach (var block in comparison.Differences)
                {
                    var start = ResultEditor.Document.CreateAnchor(block.LeftOffset);
                    start.MovementType = AnchorMovementType.BeforeInsertion;
                    start.SurviveDeletion = true;
                    var end = ResultEditor.Document.CreateAnchor(block.LeftOffset + block.LeftText.Length);
                    end.MovementType = AnchorMovementType.AfterInsertion;
                    end.SurviveDeletion = true;
                    differences.Add(new AnchoredDifference(block, start, end));
                }
                LeftEditor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinitionByExtension(Path.GetExtension(data.Left.Path));
                RightEditor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinitionByExtension(Path.GetExtension(data.Right.Path));
                ResultEditor.SyntaxHighlighting = LeftEditor.SyntaxHighlighting;
                ResultEditor.IsEnabled = SaveButton.IsEnabled = SaveAsButton.IsEnabled = true;
                selectedDifference = -1;
                if (differences.Count > 0)
                    SelectDifference(0);
                else
                {
                    DifferenceLabel.Text = "Files are identical";
                    leftRenderer.SelectedStart = rightRenderer.SelectedStart = -1;
                }
                UpdateCommands();
                UpdateTitle();
                StatusText.Text = $"{differences.Count} difference(s) | {data.Left.Encoding.WebName} | Result starts from left";
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
            if (synchronizingScroll)
                return;
            synchronizingScroll = true;
            target.ScrollToVerticalOffset(source.VerticalOffset);
            synchronizingScroll = false;
        }

        private void SelectAtRow(int row)
        {
            if (selectingDifference)
                return;
            int index = differences.FindIndex(item => row >= item.Block.StartRow && row < item.Block.StartRow + item.Block.RowCount);
            if (index >= 0 && index != selectedDifference)
                SelectDifference(index);
        }

        private void SelectDifference(int index)
        {
            if (index < 0 || index >= differences.Count)
                return;
            selectingDifference = true;
            try
            {
                selectedDifference = index;
                var selected = differences[index];
                leftRenderer.SelectedStart = rightRenderer.SelectedStart = selected.Block.StartRow;
                leftRenderer.SelectedCount = rightRenderer.SelectedCount = selected.Block.RowCount;
                LeftEditor.ScrollToLine(selected.Block.StartRow + 1);
                RightEditor.ScrollToLine(selected.Block.StartRow + 1);
                LeftEditor.TextArea.Caret.Line = selected.Block.StartRow + 1;
                RightEditor.TextArea.Caret.Line = selected.Block.StartRow + 1;
                ResultEditor.Select(selected.Start.Offset, Math.Max(0, selected.End.Offset - selected.Start.Offset));
                ResultEditor.ScrollToLine(ResultEditor.Document.GetLineByOffset(selected.Start.Offset).LineNumber);
                LeftEditor.TextArea.TextView.InvalidateLayer(ICSharpCode.AvalonEdit.Rendering.KnownLayer.Background);
                RightEditor.TextArea.TextView.InvalidateLayer(ICSharpCode.AvalonEdit.Rendering.KnownLayer.Background);
                DifferenceLabel.Text = $"Difference {index + 1} of {differences.Count}";
                UpdateCommands();
            }
            finally { selectingDifference = false; }
        }

        private void UpdateCommands()
        {
            PreviousButton.IsEnabled = selectedDifference > 0;
            NextButton.IsEnabled = selectedDifference >= 0 && selectedDifference < differences.Count - 1;
            MergeCommands.IsEnabled = selectedDifference >= 0;
        }

        private void Previous_Click(object sender, RoutedEventArgs eventArgs) => SelectDifference(selectedDifference - 1);
        private void Next_Click(object sender, RoutedEventArgs eventArgs) => SelectDifference(selectedDifference + 1);
        private void UseLeft_Click(object sender, RoutedEventArgs eventArgs) => ApplyChoice("Left");
        private void UseRight_Click(object sender, RoutedEventArgs eventArgs) => ApplyChoice("Right");
        private void UseBoth_Click(object sender, RoutedEventArgs eventArgs) => ApplyChoice("Both");

        private void ApplyChoice(string choice)
        {
            if (busy || selectedDifference < 0)
                return;
            var selected = differences[selectedDifference];
            int offset = selected.Start.Offset;
            int length = Math.Max(0, selected.End.Offset - offset);
            string current = ResultEditor.Document.GetText(offset, length);
            bool recognized = current == selected.ExpectedText || current == selected.Block.LeftText
                || current == selected.Block.RightText || current == selected.Block.BothText;
            if (!recognized && MessageBox.Show(this, "This section contains manual edits. Replace them with the selected source?",
                "Replace manual edits", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            string text = choice switch { "Left" => selected.Block.LeftText, "Right" => selected.Block.RightText, _ => selected.Block.BothText };
            ResultEditor.Document.Replace(offset, length, text);
            selected.ExpectedText = text;
            ResultEditor.Select(offset, text.Length);
            StatusText.Text = $"Applied {choice.ToLowerInvariant()} to difference {selectedDifference + 1}.";
        }

        private void Undo_Click(object sender, RoutedEventArgs eventArgs) => ResultEditor.Undo();
        private void Redo_Click(object sender, RoutedEventArgs eventArgs) => ResultEditor.Redo();

        private void Result_TextChanged(object? sender, EventArgs eventArgs)
        {
            if (!loadingResult)
                UpdateTitle();
        }

        private void Encoding_Changed(object sender, SelectionChangedEventArgs eventArgs)
        {
            if (!loadingResult && ResultEditor != null)
                UpdateTitle();
        }

        private void UpdateTitle()
        {
            if (ResultLabel == null)
                return;
            Title = (IsDirty ? "* " : "") + "Merge Tool";
            ResultLabel.Text = (IsDirty ? "* " : "") + "Merge result" + (outputPath == null ? "" : $" - {outputPath}");
            ResultLabel.ToolTip = outputPath;
        }

        private async void Save_Click(object sender, RoutedEventArgs eventArgs) => await SaveResultAsync(saveAs: outputPath == null);
        private async void SaveAs_Click(object sender, RoutedEventArgs eventArgs) => await SaveResultAsync(saveAs: true);

        private async Task<bool> SaveResultAsync(bool saveAs)
        {
            if (busy || leftFile == null)
                return false;
            string? path = outputPath;
            if (saveAs)
            {
                var dialog = new SaveFileDialog { Title = "Save merge result", FileName = Path.GetFileName(path ?? leftFile.Path), Filter = "All files|*.*", OverwritePrompt = true };
                if (dialog.ShowDialog(this) != true)
                    return false;
                path = dialog.FileName;
            }
            if (path == null)
                return false;
            if (!saveAs && File.Exists(path) && MessageBox.Show(this, $"Replace the existing result file?\n{path}", "Save result",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return false;
            string text = ResultEditor.Text;
            int encodingIndex = EncodingBox.SelectedIndex;
            Encoding encoding = encodingIndex switch
            {
                1 => new UTF8Encoding(false, true),
                2 => new UTF8Encoding(true, true),
                3 => new UnicodeEncoding(false, true, true),
                _ => leftFile.Encoding
            };
            SetBusy(true);
            try
            {
                await Task.Run(() => TextFileDocument.Save(path, text, encoding, overwrite: true));
                outputPath = path;
                savedText = text;
                savedEncodingIndex = encodingIndex;
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
            busy = value;
            Toolbar.IsEnabled = Workspace.IsEnabled = EncodingBox.IsEnabled = !value;
        }

        private bool ConfirmDiscard() => !IsDirty || MessageBox.Show(this, "Discard unsaved changes to the merge result?", "Unsaved result",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

        private void Window_Closing(object? sender, CancelEventArgs eventArgs)
        {
            if (busy)
            {
                eventArgs.Cancel = true;
                StatusText.Text = "Wait for the current operation to finish before closing.";
            }
            else
                eventArgs.Cancel = !ConfirmDiscard();
        }

        private async void Window_PreviewKeyDown(object sender, KeyEventArgs eventArgs)
        {
            if (busy)
                return;
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && eventArgs.Key == Key.S)
            {
                eventArgs.Handled = true;
                await SaveResultAsync((Keyboard.Modifiers & ModifierKeys.Shift) != 0 || outputPath == null);
            }
            else if (eventArgs.Key == Key.F7)
            {
                eventArgs.Handled = true;
                SelectDifference(selectedDifference + ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1));
            }
        }
    }
}