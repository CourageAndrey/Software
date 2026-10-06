using System.IO;
using System.Text;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace Software.MergeTool
{
    public sealed record ComparisonRow(string LeftText, string RightText, int? LeftLine, int? RightLine,
        ChangeType LeftChange, ChangeType RightChange);

    public sealed record DifferenceBlock(int StartRow, int RowCount, int LeftOffset, string LeftText, string RightText)
    {
        public string BothText => LeftText + (LeftText.Length > 0 && RightText.Length > 0
            && !LeftText.EndsWith('\n') && !LeftText.EndsWith('\r') ? "\n" : "") + RightText;
    }

    public sealed record MergeComparison(ComparisonRow[] Rows, DifferenceBlock[] Differences)
    {
        public const int MaximumLines = 20_000;

        public string LeftDisplayText => string.Join("\n", Rows.Select(row => row.LeftText));
        public string RightDisplayText => string.Join("\n", Rows.Select(row => row.RightText));

        public static MergeComparison Compare(string left, string right)
        {
            string[] leftSegments = GetSegments(left);
            string[] rightSegments = GetSegments(right);
            if (leftSegments.Length > MaximumLines || rightSegments.Length > MaximumLines)
                throw new InvalidDataException($"Compare files with at most {MaximumLines:N0} lines each.");

            var model = new SideBySideDiffBuilder().BuildDiffModel(left, right, ignoreWhitespace: false, ignoreCase: false);
            var rows = new List<ComparisonRow>();
            var blocks = new List<DifferenceBlock>();
            int leftOffset = 0;
            int startRow = -1;
            int blockOffset = 0;
            var leftBlock = new StringBuilder();
            var rightBlock = new StringBuilder();

            for (int index = 0; index < model.OldText.Lines.Count; index++)
            {
                var leftPiece = model.OldText.Lines[index];
                var rightPiece = model.NewText.Lines[index];
                string leftSegment = leftPiece.Position is int leftLine ? leftSegments[leftLine - 1] : "";
                string rightSegment = rightPiece.Position is int rightLine ? rightSegments[rightLine - 1] : "";
                bool different = leftPiece.Type != ChangeType.Unchanged || rightPiece.Type != ChangeType.Unchanged
                    || !leftSegment.Equals(rightSegment, StringComparison.Ordinal);
                if (different)
                {
                    if (startRow < 0)
                    {
                        startRow = index;
                        blockOffset = leftOffset;
                    }
                    leftBlock.Append(leftSegment);
                    rightBlock.Append(rightSegment);
                }
                else
                    FinishBlock(index);

                rows.Add(new ComparisonRow(leftPiece.Text ?? "", rightPiece.Text ?? "", leftPiece.Position, rightPiece.Position,
                    different && leftPiece.Type == ChangeType.Unchanged ? ChangeType.Modified : leftPiece.Type,
                    different && rightPiece.Type == ChangeType.Unchanged ? ChangeType.Modified : rightPiece.Type));
                leftOffset += leftSegment.Length;
            }
            FinishBlock(rows.Count);
            return new MergeComparison(rows.ToArray(), blocks.ToArray());

            void FinishBlock(int endRow)
            {
                if (startRow < 0)
                    return;
                blocks.Add(new DifferenceBlock(startRow, endRow - startRow, blockOffset, leftBlock.ToString(), rightBlock.ToString()));
                startRow = -1;
                leftBlock.Clear();
                rightBlock.Clear();
            }
        }

        private static string[] GetSegments(string text)
        {
            string[] contents = text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
            var segments = new string[contents.Length];
            int offset = 0;
            for (int index = 0; index < contents.Length; index++)
            {
                int length = contents[index].Length;
                int delimiterOffset = offset + length;
                if (delimiterOffset < text.Length)
                    length += text[delimiterOffset] == '\r' && delimiterOffset + 1 < text.Length && text[delimiterOffset + 1] == '\n' ? 2 : 1;
                segments[index] = text.Substring(offset, length);
                offset += length;
            }
            return segments;
        }
    }

    public sealed record TextFileDocument(string Path, string Text, Encoding Encoding)
    {
        public const int MaximumFileBytes = 5 * 1024 * 1024;

        public static TextFileDocument Read(string path)
        {
            string fullPath = System.IO.Path.GetFullPath(path);
            using var stream = File.OpenRead(fullPath);
            if (stream.Length > MaximumFileBytes)
                throw new InvalidDataException("Choose text files of 5 MiB or less.");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            byte[] bytes = buffer.ToArray();
            if (bytes.Length > MaximumFileBytes)
                throw new InvalidDataException("Choose text files of 5 MiB or less.");

            Encoding encoding = new UTF8Encoding(false, true);
            int preamble = 0;
            if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 }))
            {
                encoding = new UTF32Encoding(false, true, true);
                preamble = 4;
            }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xFE, 0xFF }))
            {
                encoding = new UTF32Encoding(true, true, true);
                preamble = 4;
            }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            {
                encoding = new UTF8Encoding(true, true);
                preamble = 3;
            }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
            {
                encoding = new UnicodeEncoding(false, true, true);
                preamble = 2;
            }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
            {
                encoding = new UnicodeEncoding(true, true, true);
                preamble = 2;
            }
            string text = encoding.GetString(bytes, preamble, bytes.Length - preamble);
            if (text.Contains('\0'))
                throw new InvalidDataException("Binary files are not supported.");
            return new TextFileDocument(fullPath, text, encoding);
        }

        public static void Save(string path, string text, Encoding encoding, bool overwrite = false)
        {
            string output = System.IO.Path.GetFullPath(path);
            if (File.Exists(output) && !overwrite)
                throw new IOException("The output file already exists.");
            string temporary = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(output)!, $".Software.MergeTool-{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, encoding))
                    writer.Write(text);
                if (overwrite && File.Exists(output))
                    File.Replace(temporary, output, null);
                else
                    File.Move(temporary, output);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }
    }
}