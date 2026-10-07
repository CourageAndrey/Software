using System.Globalization;
using System.IO;
using CsvHelper;
using CsvHelper.Configuration;

namespace Software.Sheets
{
    public static class ClipboardCells
    {
        public static string Encode(string value) => value.Length == 0 || value.IndexOfAny(['\t', '\r', '\n', '"']) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

        public static CellEdit[] Parse(string text, int row, int column)
        {
            if (text.Length > 2 * 1024 * 1024) throw new InvalidDataException("Clipboard text must be at most 2 Mi characters.");
            using var reader = new CsvReader(new StringReader(text), new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                Delimiter = "\t", HasHeaderRecord = false, IgnoreBlankLines = false, TrimOptions = TrimOptions.None
            });
            var edits = new List<CellEdit>();
            int offset = 0;
            while (reader.Read())
            {
                string[] values = reader.Parser.Record!;
                for (int index = 0; index < values.Length; index++)
                {
                    if (row + offset >= SpreadsheetBook.MaximumRows || column + index >= SpreadsheetBook.MaximumColumns)
                        throw new ArgumentOutOfRangeException(nameof(row), "The pasted range exceeds worksheet limits.");
                    edits.Add(new CellEdit(row + offset, column + index, values[index]));
                }
                offset++;
            }
            return edits.ToArray();
        }
    }
}