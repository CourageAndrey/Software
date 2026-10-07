using System.Globalization;
using System.IO;
using System.Text;
using NPOI.HSSF.UserModel;
using NPOI.SS.Formula;
using NPOI.SS.UserModel;
using NPOI.SS.Util;
using NPOI.XSSF.UserModel;

namespace Software.Sheets
{
	public sealed record CellEdit(int Row, int Column, string Input);
	public sealed record CellAppearance(bool Bold, bool Italic, string Foreground, string Background, string Alignment);

	public sealed class SpreadsheetBook : IDisposable
	{
		public const int MaximumRows = 10_000;
		public const int MaximumColumns = 256;
		private readonly IWorkbook _workbook;
		private readonly IFormulaEvaluator _evaluator;
		private readonly DataFormatter _formatter = new(CultureInfo.InvariantCulture);
		private readonly Stack<CellChange[]> _undo = [];
		private readonly Stack<CellChange[]> _redo = [];
		private readonly Dictionary<string, ICellStyle> _styles = [];
		private int _revision;
		private int _savedRevision;
		private int _nextRevision;

		private sealed record CellState(string Input, short Style, byte? Error = null);
		private sealed record CellChange(int Sheet, int Row, int Column, CellState Before, CellState After, int BeforeRevision, int AfterRevision);

		public string Extension => _workbook is HSSFWorkbook ? ".xls" : ".xlsx";
		public string[] SheetNames => Enumerable.Range(0, _workbook.NumberOfSheets).Select(_workbook.GetSheetName).ToArray();
		public int ActiveSheet { get; private set; }
		public bool IsDirty => _revision != _savedRevision;
		public bool CanUndo => _undo.Count > 0;
		public bool CanRedo => _redo.Count > 0;

		private SpreadsheetBook(IWorkbook workbook)
		{
			this._workbook = workbook;
			_evaluator = workbook.GetCreationHelper().CreateFormulaEvaluator();
			ActiveSheet = Math.Clamp(workbook.ActiveSheetIndex, 0, workbook.NumberOfSheets - 1);
		}

		public static SpreadsheetBook New(bool legacy = false)
		{
			IWorkbook workbook = legacy ? new HSSFWorkbook() : new XSSFWorkbook();
			workbook.CreateSheet("Sheet1");
			return new SpreadsheetBook(workbook);
		}

		public static SpreadsheetBook Open(string path)
		{
			Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
			string extension = Path.GetExtension(path).ToLowerInvariant();
			if (extension is not (".xls" or ".xlsx")) throw new NotSupportedException("Open XLS or XLSX workbooks.");
			if (new FileInfo(path).Length > 20L * 1024 * 1024) throw new InvalidDataException("Choose workbooks of 20 MiB or less.");
			if (extension == ".xlsx")
			{
				using var zip = System.IO.Compression.ZipFile.OpenRead(path);
				long size = 0;
				foreach (var entry in zip.Entries)
				{
					if (zip.Entries.Count > 10_000 || entry.Length > 100L * 1024 * 1024 - size)
						throw new InvalidDataException("The workbook exceeds package safety limits.");
					size += entry.Length;
				}
			}
			using var stream = File.OpenRead(path);
			IWorkbook workbook = extension == ".xls" ? new HSSFWorkbook(stream) : new XSSFWorkbook(stream);
			try
			{
				if (workbook.NumberOfSheets is < 1 or > 32) throw new InvalidDataException("Workbooks must contain 1-32 worksheets.");
				int cells = 0;
				for (int index = 0; index < workbook.NumberOfSheets; index++)
				{
					var sheet = workbook.GetSheetAt(index);
					if (sheet.LastRowNum >= MaximumRows) throw new InvalidDataException("Worksheets exceeding 10,000 rows are not supported.");
					foreach (IRow row in sheet)
					{
						if (row.LastCellNum > MaximumColumns) throw new InvalidDataException("Worksheets exceeding 256 columns are not supported.");
						cells += row.PhysicalNumberOfCells;
						if (cells > 250_000) throw new InvalidDataException("Workbooks exceeding 250,000 stored cells are not supported.");
					}
				}
				return new SpreadsheetBook(workbook);
			}
			catch { workbook.Dispose(); throw; }
		}

		public void SelectSheet(int index)
		{
			if (index < 0 || index >= _workbook.NumberOfSheets) throw new ArgumentOutOfRangeException(nameof(index));
			ActiveSheet = index;
			_workbook.SetActiveSheet(index);
		}

		public (int Rows, int Columns) Dimensions()
		{
			var sheet = _workbook.GetSheetAt(ActiveSheet);
			int columns = 0;
			foreach (IRow row in sheet) columns = Math.Max(columns, row.LastCellNum);
			return (Math.Min(MaximumRows, Math.Max(100, sheet.LastRowNum + 26)), Math.Min(MaximumColumns, Math.Max(26, columns + 5)));
		}

		private ICell? Cell(int row, int column) => _workbook.GetSheetAt(ActiveSheet).GetRow(row)?.GetCell(column);

		public string Input(int row, int column)
		{
			var cell = Cell(row, column);
			return cell?.CellType switch
			{
				CellType.Formula => "=" + cell.CellFormula,
				CellType.Numeric => cell.NumericCellValue.ToString("R", CultureInfo.InvariantCulture),
				CellType.Boolean => cell.BooleanCellValue ? "TRUE" : "FALSE",
				CellType.String => cell.StringCellValue,
				_ => ""
			};
		}

		public string EditableInput(int row, int column)
		{
			string input = Input(row, column);
			if (Cell(row, column)?.CellType == CellType.String && (input.StartsWith('=') || input.StartsWith('\'')
				|| bool.TryParse(input, out _) || double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
				return "'" + input;
			return input;
		}

		public string Display(int row, int column)
		{
			var cell = Cell(row, column);
			if (cell == null) return "";
			try { return _formatter.FormatCellValue(cell, _evaluator); }
			catch (Exception exception) when (exception is NotImplementedException or InvalidOperationException or ArgumentException)
			{
				return cell.CellType == CellType.Formula ? "#UNSUPPORTED" : _formatter.FormatCellValue(cell);
			}
		}

		public CellAppearance Appearance(int row, int column)
		{
			var cell = Cell(row, column);
			if (cell == null) return new(false, false, "#111827", "#FFFFFF", "Left");
			var style = cell.CellStyle;
			var font = _workbook.GetFontAt(style.FontIndex);
			return new CellAppearance(font.IsBold, font.IsItalic, IndexedColor(font.Color, "#111827"),
				style.FillPattern == FillPattern.SolidForeground ? IndexedColor(style.FillForegroundColor, "#FFFFFF") : "#FFFFFF", style.Alignment.ToString());
		}

		public void Edit(IEnumerable<CellEdit> edits)
		{
			var planned = edits.ToArray();
			foreach (var edit in planned)
			{
				CheckAddress(edit.Row, edit.Column);
				if (edit.Input.Length > 32767) throw new ArgumentException("Cell text must be at most 32,767 characters.");
				if (edit.Input.StartsWith('='))
				{
					IFormulaParsingWorkbook parsing = _workbook is XSSFWorkbook xlsx ? XSSFEvaluationWorkbook.Create(xlsx) : HSSFEvaluationWorkbook.Create((HSSFWorkbook)_workbook);
					FormulaParser.Parse(edit.Input[1..], parsing, FormulaType.Cell, ActiveSheet);
				}
			}
			var changes = planned.GroupBy(edit => (edit.Row, edit.Column)).Select(group => group.Last()).Select(edit =>
			{
				var before = Capture(edit.Row, edit.Column);
				return new CellChange(ActiveSheet, edit.Row, edit.Column, before, new CellState(edit.Input, before.Style), _revision, _nextRevision + 1);
			}).ToArray();
			if (changes.Length == 0) return;
			try { foreach (var change in changes) Apply(change.Row, change.Column, change.After); }
			catch { foreach (var change in changes) Apply(change.Row, change.Column, change.Before); throw; }
			Commit(changes);
		}

		public void Format(IEnumerable<(int Row, int Column)> addresses, bool? bold = null, bool? italic = null, string? numberFormat = null, string? alignment = null)
		{
			var changes = new List<CellChange>();
			foreach (var (row, column) in addresses.Distinct())
			{
				CheckAddress(row, column);
				var before = Capture(row, column);
				var oldStyle = _workbook.GetCellStyleAt(before.Style);
				string key = $"{before.Style}:{bold}:{italic}:{numberFormat}:{alignment}";
				if (!_styles.TryGetValue(key, out var style))
				{
					if (_workbook.NumCellStyles >= 3500) throw new InvalidOperationException("The workbook has too many styles to add more formatting safely.");
					style = _workbook.CreateCellStyle();
					style.CloneStyleFrom(oldStyle);
					if (bold != null || italic != null)
					{
						var oldFont = _workbook.GetFontAt(oldStyle.FontIndex);
						var font = _workbook.CreateFont();
						font.FontName = oldFont.FontName; font.FontHeightInPoints = oldFont.FontHeightInPoints;
						font.IsBold = bold ?? oldFont.IsBold; font.IsItalic = italic ?? oldFont.IsItalic;
						font.Color = oldFont.Color; font.Underline = oldFont.Underline; font.IsStrikeout = oldFont.IsStrikeout;
						style.SetFont(font);
					}
					if (numberFormat != null) style.DataFormat = _workbook.CreateDataFormat().GetFormat(numberFormat);
					if (alignment != null) style.Alignment = Enum.Parse<HorizontalAlignment>(alignment);
					_styles.Add(key, style);
				}
				changes.Add(new CellChange(ActiveSheet, row, column, before, before with { Style = style.Index }, _revision, _nextRevision + 1));
			}
			if (changes.Count == 0) return;
			foreach (var change in changes) Apply(change.Row, change.Column, change.After);
			Commit(changes.ToArray());
		}

		private CellState Capture(int row, int column)
		{
			var cell = Cell(row, column);
			return new CellState(cell?.CellType == CellType.String ? "'" + cell.StringCellValue : Input(row, column), cell?.CellStyle.Index ?? 0,
				cell?.CellType == CellType.Error ? cell.ErrorCellValue : null);
		}

		private void Apply(int row, int column, CellState value)
		{
			var sheet = _workbook.GetSheetAt(ActiveSheet);
			var targetRow = sheet.GetRow(row) ?? sheet.CreateRow(row);
			var cell = targetRow.GetCell(column) ?? targetRow.CreateCell(column);
			cell.SetCellType(CellType.Blank);
			if (value.Error is byte error) cell.SetCellErrorValue(error);
			else if (value.Input.StartsWith('=')) cell.SetCellFormula(value.Input[1..]);
			else if (value.Input.StartsWith('\'')) cell.SetCellValue(value.Input[1..]);
			else if (bool.TryParse(value.Input, out bool boolean)) cell.SetCellValue(boolean);
			else if (double.TryParse(value.Input, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number)) cell.SetCellValue(number);
			else if (value.Input.Length > 0) cell.SetCellValue(value.Input);
			cell.CellStyle = _workbook.GetCellStyleAt(value.Style);
		}

		private void Commit(CellChange[] changes)
		{
			_revision = ++_nextRevision;
			_undo.Push(changes); _redo.Clear(); _evaluator.ClearAllCachedResultValues();
		}

		public void Undo() => Replay(_undo, _redo, forward: false);
		public void Redo() => Replay(_redo, _undo, forward: true);

		private void Replay(Stack<CellChange[]> source, Stack<CellChange[]> destination, bool forward)
		{
			if (source.Count == 0) return;
			var changes = source.Pop();
			SelectSheet(changes[0].Sheet);
			foreach (var change in changes) Apply(change.Row, change.Column, forward ? change.After : change.Before);
			_revision = forward ? changes[0].AfterRevision : changes[0].BeforeRevision;
			destination.Push(changes); _evaluator.ClearAllCachedResultValues();
		}

		public void AddSheet(string name)
		{
			if (_workbook.NumberOfSheets >= 32) throw new InvalidOperationException("At most 32 worksheets are supported.");
			WorkbookUtil.ValidateSheetName(name);
			_workbook.CreateSheet(name); SelectSheet(_workbook.NumberOfSheets - 1); StructureChanged();
		}

		public void RenameSheet(string name) { WorkbookUtil.ValidateSheetName(name); _workbook.SetSheetName(ActiveSheet, name); StructureChanged(); }
		public void DeleteSheet()
		{
			if (_workbook.NumberOfSheets == 1) throw new InvalidOperationException("Keep at least one worksheet.");
			_workbook.RemoveSheetAt(ActiveSheet); SelectSheet(Math.Min(ActiveSheet, _workbook.NumberOfSheets - 1)); StructureChanged();
		}
		private void StructureChanged() { _revision = ++_nextRevision; _undo.Clear(); _redo.Clear(); _evaluator.ClearAllCachedResultValues(); }

		public void Save(string path, bool overwrite = false)
		{
			string output = Path.GetFullPath(path);
			if (!Path.GetExtension(output).Equals(Extension, StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException($"Save this workbook as {Extension}; cross-format conversion is not supported.");
			if (!overwrite && File.Exists(output)) throw new IOException("The output file already exists.");
			string temporary = Path.Combine(Path.GetDirectoryName(output)!, $".Software.Sheets-{Guid.NewGuid():N}.tmp");
			try
			{
				for (int index = 0; index < _workbook.NumberOfSheets; index++) _workbook.GetSheetAt(index).ForceFormulaRecalculation = true;
				using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) _workbook.Write(stream, leaveOpen: true);
				if (overwrite && File.Exists(output)) File.Replace(temporary, output, null); else File.Move(temporary, output);
				_savedRevision = _revision;
			}
			finally { if (File.Exists(temporary)) File.Delete(temporary); }
		}

		public static string Address(int row, int column) => new CellReference(row, column).FormatAsString();
		public static (int Row, int Column) ParseAddress(string address)
		{
			var reference = new CellReference(address);
			if (reference.SheetName != null) throw new ArgumentException("Enter an address in the current sheet, such as A1.");
			CheckAddress(reference.Row, reference.Col);
			return (reference.Row, reference.Col);
		}
		private static void CheckAddress(int row, int column) { if (row is < 0 or >= MaximumRows || column is < 0 or >= MaximumColumns) throw new ArgumentOutOfRangeException(nameof(row), "Use rows 1-10,000 and columns A-IV."); }
		private static string IndexedColor(short color, string fallback) => color switch { 8 => "#000000", 9 => "#FFFFFF", 10 => "#FF0000", 11 => "#008000", 12 => "#0000FF", 13 => "#FFFF00", 22 => "#C0C0C0", 23 => "#808080", _ => fallback };
		public void Dispose() => _workbook.Dispose();
	}
}