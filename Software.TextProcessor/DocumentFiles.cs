using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using NPOI.HWPF.Extractor;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Software.TextProcessor
{
	public sealed record LoadedDocument(FlowDocument Document, string? ImportNote);

	public static class DocumentFiles
	{
		public const long MaximumFileBytes = 20L * 1024 * 1024;
		private const long _maximumExpandedBytes = 100L * 1024 * 1024;

		static DocumentFiles() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

		public static FlowDocument NewDocument() => new()
		{
			FontFamily = new FontFamily("Calibri"), FontSize = 16, PagePadding = new Thickness(48),
			ColumnWidth = double.PositiveInfinity, Background = Brushes.White
		};

		public static LoadedDocument Open(string path)
		{
			if (new FileInfo(path).Length > MaximumFileBytes)
				throw new InvalidDataException("Choose documents of 20 MiB or less.");
			string extension = Path.GetExtension(path).ToLowerInvariant();
			var document = NewDocument();
			switch (extension)
			{
				case ".rtf":
					using (var input = File.OpenRead(path))
						new TextRange(document.ContentStart, document.ContentEnd).Load(input, DataFormats.Rtf);
					return new LoadedDocument(document, null);
				case ".txt":
					using (var reader = new StreamReader(path, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true))
						AddText(document, reader.ReadToEnd());
					return new LoadedDocument(document, "Plain-text files are saved as UTF-8. Formatting requires RTF or DOCX.");
				case ".doc":
					using (var input = File.OpenRead(path))
					{
						var extractor = new WordExtractor(input);
						string text = extractor.Text.Replace('\u0007', '\t').Replace('\u000B', '\n');
						AddText(document, new string(text.Where(character => !char.IsControl(character) || character is '\r' or '\n' or '\t').ToArray()));
					}
					return new LoadedDocument(document, "Legacy DOC imported as text. Formatting and images are not retained. Save an editable copy as DOCX or RTF.");
				case ".docx":
					ValidatePackage(path);
					using (var word = WordprocessingDocument.Open(path, false))
					{
						var main = word.MainDocumentPart ?? throw new InvalidDataException("The document has no main part.");
						var body = main.Document.Body ?? throw new InvalidDataException("The document has no body.");
						foreach (var block in body.ChildElements)
						{
							if (block is W.Paragraph paragraph)
								AddWordParagraph(document.Blocks, paragraph, main);
							else if (block is W.Table table)
								document.Blocks.Add(ReadTable(table, main));
						}
					}
					return new LoadedDocument(document, "DOCX import preserves common text formatting, lists, tables, and embedded images. Advanced Word layout, headers/footers, styles, and revisions may not be retained.");
				default:
					throw new NotSupportedException("Open RTF, TXT, DOC, or DOCX documents.");
			}
		}

		public static void Save(string path, FlowDocument document, bool overwrite = false)
		{
			string output = Path.GetFullPath(path);
			string extension = Path.GetExtension(output).ToLowerInvariant();
			if (extension is not (".rtf" or ".txt" or ".docx"))
				throw new NotSupportedException("Save as RTF, TXT, or DOCX. Legacy DOC is import-only.");
			if (File.Exists(output) && !overwrite)
				throw new IOException("The destination already exists.");
			string temporary = Path.Combine(Path.GetDirectoryName(output)!, $".Software.TextProcessor-{Guid.NewGuid():N}.tmp");
			try
			{
				if (extension == ".docx")
					WriteDocx(temporary, document);
				else
				{
					using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
					var range = new TextRange(document.ContentStart, document.ContentEnd);
					if (extension == ".rtf")
						range.Save(stream, DataFormats.Rtf);
					else
					{
						using var writer = new StreamWriter(stream, new UTF8Encoding(false, true));
						writer.Write(range.Text);
					}
				}
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

		private static void AddText(FlowDocument document, string text)
		{
			foreach (string line in text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None))
				document.Blocks.Add(new Paragraph(new Run(line)) { Margin = new Thickness(0, 0, 0, 4) });
		}

		private static void ValidatePackage(string path)
		{
			using var zip = System.IO.Compression.ZipFile.OpenRead(path);
			if (zip.Entries.Count > 10_000)
				throw new InvalidDataException("The DOCX contains too many package entries.");
			long total = 0;
			foreach (var entry in zip.Entries)
			{
				if (entry.Length > _maximumExpandedBytes - total)
					throw new InvalidDataException("The DOCX exceeds the 100 MiB expanded-size limit.");
				total += entry.Length;
			}
		}

		private static void AddWordParagraph(BlockCollection blocks, W.Paragraph source, MainDocumentPart main)
		{
			Paragraph paragraph = ReadParagraph(source, main);
			var numbering = source.ParagraphProperties?.NumberingProperties;
			if (numbering != null)
			{
				var marker = TextMarkerStyle.Disc;
				var numberId = numbering.NumberingId?.Val?.Value;
				var instance = main.NumberingDefinitionsPart?.Numbering.Elements<W.NumberingInstance>().FirstOrDefault(item => item.NumberID?.Value == numberId);
				var abstractId = instance?.AbstractNumId?.Val?.Value;
				var definition = main.NumberingDefinitionsPart?.Numbering.Elements<W.AbstractNum>().FirstOrDefault(item => item.AbstractNumberId?.Value == abstractId);
				int level = numbering.NumberingLevelReference?.Val?.Value ?? 0;
				var format = definition?.Elements<W.Level>().FirstOrDefault(item => item.LevelIndex?.Value == level)?.NumberingFormat?.Val?.Value;
				if (format == W.NumberFormatValues.Decimal)
					marker = TextMarkerStyle.Decimal;
				if (blocks.LastBlock is System.Windows.Documents.List prior && prior.MarkerStyle == marker)
					prior.ListItems.Add(new ListItem(paragraph));
				else
				{
					var list = new System.Windows.Documents.List { MarkerStyle = marker };
					list.ListItems.Add(new ListItem(paragraph));
					blocks.Add(list);
				}
			}
			else
				blocks.Add(paragraph);
		}

		private static Paragraph ReadParagraph(W.Paragraph source, MainDocumentPart main)
		{
			var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
			var alignment = source.ParagraphProperties?.Justification?.Val?.Value;
			if (alignment == W.JustificationValues.Center) paragraph.TextAlignment = TextAlignment.Center;
			else if (alignment == W.JustificationValues.Right) paragraph.TextAlignment = TextAlignment.Right;
			else if (alignment == W.JustificationValues.Both) paragraph.TextAlignment = TextAlignment.Justify;
			foreach (var wordRun in source.Descendants<W.Run>())
			{
				var properties = wordRun.RunProperties;
				foreach (var item in wordRun.ChildElements)
				{
					Inline? inline = item switch
					{
						W.Text text => new Run(text.Text),
						W.TabChar => new Run("\t"),
						W.Break => new LineBreak(),
						W.Drawing drawing => ReadImage(drawing, main),
						_ => null
					};
					if (inline == null)
						continue;
					if (properties != null)
					{
						if (properties.Bold != null && properties.Bold.Val?.Value != false) inline.FontWeight = FontWeights.Bold;
						if (properties.Italic != null && properties.Italic.Val?.Value != false) inline.FontStyle = FontStyles.Italic;
						if (properties.Underline != null && properties.Underline.Val?.Value != W.UnderlineValues.None) inline.TextDecorations = TextDecorations.Underline;
						if (properties.Strike != null && properties.Strike.Val?.Value != false) inline.TextDecorations = TextDecorations.Strikethrough;
						if (double.TryParse(properties.FontSize?.Val?.Value, System.Globalization.CultureInfo.InvariantCulture, out double size) && size is >= 2 and <= 400)
							inline.FontSize = size * 2 / 3;
						string? font = properties.RunFonts?.Ascii?.Value;
						if (!string.IsNullOrEmpty(font)) inline.FontFamily = new FontFamily(font);
						string? color = properties.Color?.Val?.Value;
						if (color?.Length == 6 && int.TryParse(color, System.Globalization.NumberStyles.HexNumber, null, out int rgb))
							inline.Foreground = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
					}
					paragraph.Inlines.Add(inline);
				}
			}
			return paragraph;
		}

		private static Inline ReadImage(W.Drawing drawing, MainDocumentPart main)
		{
			string? relationship = drawing.Descendants<DocumentFormat.OpenXml.Drawing.Blip>().FirstOrDefault()?.Embed?.Value;
			if (relationship == null || main.GetPartById(relationship) is not ImagePart part)
				return new Run("[Unsupported image]");
			try
			{
				using var stream = part.GetStream();
				using var buffered = new MemoryStream();
				stream.CopyTo(buffered);
				buffered.Position = 0;
				var bitmap = new BitmapImage();
				bitmap.BeginInit();
				bitmap.CacheOption = BitmapCacheOption.OnLoad;
				bitmap.DecodePixelWidth = 1200;
				bitmap.StreamSource = buffered;
				bitmap.EndInit();
				bitmap.Freeze();
				var extent = drawing.Descendants<DocumentFormat.OpenXml.Drawing.Wordprocessing.Extent>().FirstOrDefault();
				double width = extent?.Cx?.Value / 9525d ?? Math.Min(bitmap.Width, 650);
				return new InlineUIContainer(new Image { Source = bitmap, Width = Math.Clamp(width, 1, 800), Stretch = Stretch.Uniform });
			}
			catch (Exception exception) when (exception is NotSupportedException or IOException or ArgumentException)
			{
				return new Run("[Unsupported image]");
			}
		}

		private static Table ReadTable(W.Table source, MainDocumentPart main)
		{
			var table = new Table { CellSpacing = 0 };
			var group = new TableRowGroup();
			foreach (var row in source.Elements<W.TableRow>())
			{
				var targetRow = new TableRow();
				foreach (var cell in row.Elements<W.TableCell>())
				{
					var targetCell = new TableCell { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), Padding = new Thickness(5) };
					foreach (var paragraph in cell.Elements<W.Paragraph>())
						targetCell.Blocks.Add(ReadParagraph(paragraph, main));
					if (targetCell.Blocks.Count == 0) targetCell.Blocks.Add(new Paragraph());
					targetRow.Cells.Add(targetCell);
				}
				group.Rows.Add(targetRow);
			}
			table.RowGroups.Add(group);
			return table;
		}

		private static void WriteDocx(string path, FlowDocument document)
		{
			using var word = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
			var main = word.AddMainDocumentPart();
			var body = new W.Body();
			main.Document = new W.Document(body);
			int imageId = 0;
			WriteBlocks(document.Blocks, body, main, ref imageId);
			body.Append(new W.SectionProperties(new W.PageSize { Width = 12240, Height = 15840 }, new W.PageMargin { Top = 1440, Right = 1440, Bottom = 1440, Left = 1440 }));
			main.Document.Save();
		}

		private static void WriteBlocks(BlockCollection blocks, OpenXmlCompositeElement parent, MainDocumentPart main, ref int imageId)
		{
			foreach (Block block in blocks)
			{
				if (block is Paragraph paragraph)
					parent.Append(WriteParagraph(paragraph, main, ref imageId));
				else if (block is Section section)
					WriteBlocks(section.Blocks, parent, main, ref imageId);
				else if (block is System.Windows.Documents.List list)
				{
					int numberId = CreateNumbering(main, list);
					foreach (var item in list.ListItems)
					{
						bool first = true;
						foreach (Block child in item.Blocks)
						{
							if (child is Paragraph itemParagraph)
							{
								var output = WriteParagraph(itemParagraph, main, ref imageId);
								if (first)
								{
									output.ParagraphProperties!.AddChild(new W.NumberingProperties(new W.NumberingLevelReference { Val = 0 }, new W.NumberingId { Val = numberId }), true);
									first = false;
								}
								parent.Append(output);
							}
						}
					}
				}
				else if (block is Table table)
				{
					var target = new W.Table(new W.TableProperties(new W.TableBorders(
						new W.TopBorder { Val = W.BorderValues.Single, Size = 4 }, new W.LeftBorder { Val = W.BorderValues.Single, Size = 4 },
						new W.BottomBorder { Val = W.BorderValues.Single, Size = 4 }, new W.RightBorder { Val = W.BorderValues.Single, Size = 4 },
						new W.InsideHorizontalBorder { Val = W.BorderValues.Single, Size = 4 }, new W.InsideVerticalBorder { Val = W.BorderValues.Single, Size = 4 })));
					int columns = Math.Max(1, table.RowGroups.SelectMany(group => group.Rows).Select(row => row.Cells.Count).DefaultIfEmpty(1).Max());
					target.Append(new W.TableGrid(Enumerable.Range(0, columns).Select(_ => new W.GridColumn { Width = (9000 / columns).ToString(System.Globalization.CultureInfo.InvariantCulture) })));
					foreach (var group in table.RowGroups)
					{
						foreach (var row in group.Rows)
						{
							var targetRow = new W.TableRow();
							foreach (var cell in row.Cells)
							{
								var targetCell = new W.TableCell();
								WriteBlocks(cell.Blocks, targetCell, main, ref imageId);
								if (targetCell.LastChild is not W.Paragraph) targetCell.Append(new W.Paragraph());
								targetRow.Append(targetCell);
							}
							target.Append(targetRow);
						}
					}
					parent.Append(target);
				}
				else
					parent.Append(new W.Paragraph(new W.Run(new W.Text(new TextRange(block.ContentStart, block.ContentEnd).Text))));
			}
		}

		private static int CreateNumbering(MainDocumentPart main, System.Windows.Documents.List list)
		{
			var part = main.NumberingDefinitionsPart ?? main.AddNewPart<NumberingDefinitionsPart>();
			part.Numbering ??= new W.Numbering();
			int id = part.Numbering.Elements<W.NumberingInstance>().Count() + 1;
			bool numbered = list.MarkerStyle == TextMarkerStyle.Decimal;
			var level = new W.Level(new W.StartNumberingValue { Val = list.StartIndex },
				new W.NumberingFormat { Val = numbered ? W.NumberFormatValues.Decimal : W.NumberFormatValues.Bullet },
				new W.LevelText { Val = numbered ? "%1." : "\u2022" }) { LevelIndex = 0 };
			var definition = new W.AbstractNum(level) { AbstractNumberId = id };
			var firstInstance = part.Numbering.Elements<W.NumberingInstance>().FirstOrDefault();
			if (firstInstance == null) part.Numbering.Append(definition); else part.Numbering.InsertBefore(definition, firstInstance);
			part.Numbering.Append(new W.NumberingInstance(new W.AbstractNumId { Val = id }) { NumberID = id });
			return id;
		}

		private static W.Paragraph WriteParagraph(Paragraph source, MainDocumentPart main, ref int imageId)
		{
			var alignment = source.TextAlignment switch
			{
				TextAlignment.Center => W.JustificationValues.Center,
				TextAlignment.Right => W.JustificationValues.Right,
				TextAlignment.Justify => W.JustificationValues.Both,
				_ => W.JustificationValues.Left
			};
			var paragraph = new W.Paragraph(new W.ParagraphProperties(new W.Justification { Val = alignment }));
			WriteInlines(source.Inlines, paragraph, main, ref imageId);
			return paragraph;
		}

		private static void WriteInlines(InlineCollection inlines, W.Paragraph paragraph, MainDocumentPart main, ref int imageId)
		{
			foreach (Inline inline in inlines)
			{
				if (inline is Span span)
				{
					WriteInlines(span.Inlines, paragraph, main, ref imageId);
					continue;
				}
				var properties = new W.RunProperties(new W.RunFonts { Ascii = inline.FontFamily.Source, HighAnsi = inline.FontFamily.Source },
					new W.FontSize { Val = Math.Round(inline.FontSize * 1.5).ToString(System.Globalization.CultureInfo.InvariantCulture) });
				if (inline.FontWeight.ToOpenTypeWeight() >= 600) properties.AddChild(new W.Bold(), true);
				if (inline.FontStyle == FontStyles.Italic) properties.AddChild(new W.Italic(), true);
				if (inline.TextDecorations.Contains(TextDecorations.Underline[0])) properties.AddChild(new W.Underline { Val = W.UnderlineValues.Single }, true);
				if (inline.TextDecorations.Contains(TextDecorations.Strikethrough[0])) properties.AddChild(new W.Strike(), true);
				if (inline.Foreground is SolidColorBrush foreground)
					properties.AddChild(new W.Color { Val = $"{foreground.Color.R:X2}{foreground.Color.G:X2}{foreground.Color.B:X2}" }, true);
				var run = new W.Run(properties);
				if (inline is Run text)
				{
					string[] lines = text.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
					for (int index = 0; index < lines.Length; index++)
					{
						if (index > 0) run.Append(new W.Break());
						string[] tabs = lines[index].Split('\t');
						for (int tab = 0; tab < tabs.Length; tab++)
						{
							if (tab > 0) run.Append(new W.TabChar());
							run.Append(new W.Text(tabs[tab]) { Space = SpaceProcessingModeValues.Preserve });
						}
					}
				}
				else if (inline is LineBreak)
					run.Append(new W.Break());
				else if (inline is InlineUIContainer { Child: Image image } && image.Source is BitmapSource bitmap)
					run.Append(WriteImage(image, bitmap, main, ++imageId));
				paragraph.Append(run);
			}
		}

		private static W.Drawing WriteImage(Image image, BitmapSource bitmap, MainDocumentPart main, int id)
		{
			var part = main.AddImagePart(ImagePartType.Png);
			var encoder = new PngBitmapEncoder();
			encoder.Frames.Add(BitmapFrame.Create(bitmap));
			using (var data = new MemoryStream()) { encoder.Save(data); data.Position = 0; part.FeedData(data); }
			double width = double.IsNaN(image.Width) ? Math.Min(bitmap.Width, 650) : image.Width;
			double height = double.IsNaN(image.Height) ? width * bitmap.PixelHeight / Math.Max(1, bitmap.PixelWidth) : image.Height;
			long cx = (long)(width * 9525), cy = (long)(height * 9525);
			var picture = new DocumentFormat.OpenXml.Drawing.Pictures.Picture(
				new DocumentFormat.OpenXml.Drawing.Pictures.NonVisualPictureProperties(
					new DocumentFormat.OpenXml.Drawing.Pictures.NonVisualDrawingProperties { Id = (uint)id, Name = $"Image {id}" },
					new DocumentFormat.OpenXml.Drawing.Pictures.NonVisualPictureDrawingProperties()),
				new DocumentFormat.OpenXml.Drawing.Pictures.BlipFill(new DocumentFormat.OpenXml.Drawing.Blip { Embed = main.GetIdOfPart(part) },
					new DocumentFormat.OpenXml.Drawing.Stretch(new DocumentFormat.OpenXml.Drawing.FillRectangle())),
				new DocumentFormat.OpenXml.Drawing.Pictures.ShapeProperties(
					new DocumentFormat.OpenXml.Drawing.Transform2D(new DocumentFormat.OpenXml.Drawing.Offset { X = 0, Y = 0 }, new DocumentFormat.OpenXml.Drawing.Extents { Cx = cx, Cy = cy }),
					new DocumentFormat.OpenXml.Drawing.PresetGeometry(new DocumentFormat.OpenXml.Drawing.AdjustValueList()) { Preset = DocumentFormat.OpenXml.Drawing.ShapeTypeValues.Rectangle }));
			return new W.Drawing(new DocumentFormat.OpenXml.Drawing.Wordprocessing.Inline(
				new DocumentFormat.OpenXml.Drawing.Wordprocessing.Extent { Cx = cx, Cy = cy },
				new DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties { Id = (uint)id, Name = $"Image {id}" },
				new DocumentFormat.OpenXml.Drawing.Graphic(new DocumentFormat.OpenXml.Drawing.GraphicData(picture) { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" })));
		}
	}
}