using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DiffPlex.DiffBuilder.Model;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;

namespace Software.MergeTool
{
	internal sealed class DiffBackgroundRenderer(bool leftSide) : IBackgroundRenderer
	{
		private static readonly Brush _deleted = new SolidColorBrush(Color.FromRgb(253, 228, 226));
		private static readonly Brush _inserted = new SolidColorBrush(Color.FromRgb(221, 243, 226));
		private static readonly Brush _modified = new SolidColorBrush(Color.FromRgb(255, 241, 201));
		private static readonly Brush _placeholder = new SolidColorBrush(Color.FromRgb(237, 240, 242));
		private static readonly Brush _selected = new SolidColorBrush(Color.FromRgb(37, 107, 123));

		public ComparisonRow[] Rows { get; set; } = [];
		public int SelectedStart { get; set; } = -1;
		public int SelectedCount { get; set; }
		public KnownLayer Layer => KnownLayer.Background;

		public void Draw(TextView textView, DrawingContext drawingContext)
		{
			if (!textView.VisualLinesValid)
			{
				return;
			}

			foreach (var line in textView.VisualLines)
			{
				int index = line.FirstDocumentLine.LineNumber - 1;
				if (index >= Rows.Length)
				{
					continue;
				}

				ChangeType change = leftSide ? Rows[index].LeftChange : Rows[index].RightChange;
				Brush? background = change switch
				{
					ChangeType.Deleted => _deleted,
					ChangeType.Inserted => _inserted,
					ChangeType.Modified => _modified,
					ChangeType.Imaginary => _placeholder,
					_ => null
				};
				double top = line.VisualTop - textView.VerticalOffset;
				if (background != null)
				{
					drawingContext.DrawRectangle(background, null, new Rect(0, top, textView.ActualWidth, line.Height));
				}

				if (index >= SelectedStart && index < SelectedStart + SelectedCount)
				{
					drawingContext.DrawRectangle(_selected, null, new Rect(0, top, 3, line.Height));
				}
			}
		}
	}

	internal sealed class SourceLineMargin(TextEditor editor, bool leftSide) : AbstractMargin
	{
		public ComparisonRow[] Rows { get; set; } = [];

		protected override Size MeasureOverride(Size availableSize) => new(54, 0);

		protected override void OnTextViewChanged(TextView oldTextView, TextView newTextView)
		{
			if (oldTextView != null)
			{
				oldTextView.VisualLinesChanged -= ViewChanged;
				oldTextView.ScrollOffsetChanged -= ViewChanged;
			}
			base.OnTextViewChanged(oldTextView, newTextView);
			if (newTextView != null)
			{
				newTextView.VisualLinesChanged += ViewChanged;
				newTextView.ScrollOffsetChanged += ViewChanged;
			}
		}

		private void ViewChanged(object? sender, EventArgs eventArgs) => InvalidateVisual();

		protected override void OnRender(DrawingContext drawingContext)
		{
			drawingContext.DrawRectangle(new SolidColorBrush(Color.FromRgb(248, 250, 251)), null, new Rect(RenderSize));
			if (TextView == null || !TextView.VisualLinesValid)
			{
				return;
			}

			foreach (var line in TextView.VisualLines)
			{
				int index = line.FirstDocumentLine.LineNumber - 1;
				if (index >= Rows.Length)
				{
					continue;
				}

				int? number = leftSide ? Rows[index].LeftLine : Rows[index].RightLine;
				if (number == null)
				{
					continue;
				}

				var text = new FormattedText(number.Value.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
					FlowDirection.LeftToRight, new Typeface(editor.FontFamily, editor.FontStyle, editor.FontWeight, editor.FontStretch),
					editor.FontSize, Brushes.Gray, VisualTreeHelper.GetDpi(this).PixelsPerDip);
				drawingContext.DrawText(text, new Point(46 - text.Width, line.VisualTop - TextView.VerticalOffset));
			}
		}
	}
}