using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace Software.Sheets
{
	public sealed class SheetRow(SpreadsheetBook book, int index)
	{
		private readonly Dictionary<int, SheetCell> _cells = [];
		public int Index { get; } = index;
		public int Number => Index + 1;
		public SheetCell this[int column] => _cells.TryGetValue(column, out var cell) ? cell : _cells[column] = new SheetCell(book, Index, column);
		public void Refresh() { foreach (var cell in _cells.Values) cell.Refresh(); }
	}

	public sealed class SheetCell(SpreadsheetBook book, int row, int column) : INotifyPropertyChanged
	{
		public string Display => book.Display(row, column);
		public string Input => book.EditableInput(row, column);
		public FontWeight Weight => book.Appearance(row, column).Bold ? FontWeights.Bold : FontWeights.Normal;
		public FontStyle Style => book.Appearance(row, column).Italic ? FontStyles.Italic : FontStyles.Normal;
		public Brush Foreground => (Brush)new BrushConverter().ConvertFromString(book.Appearance(row, column).Foreground)!;
		public Brush Background => (Brush)new BrushConverter().ConvertFromString(book.Appearance(row, column).Background)!;
		public TextAlignment Alignment => book.Appearance(row, column).Alignment switch { "Center" => TextAlignment.Center, "Right" => TextAlignment.Right, _ => TextAlignment.Left };
		public event PropertyChangedEventHandler? PropertyChanged;
		public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
	}
}