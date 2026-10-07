using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Highlighting;

namespace Software.Notepad
{
	public sealed class EditorDocument : TextEditor, IDisposable
	{
		private readonly FoldingManager _folding;
		private readonly DispatcherTimer _foldingTimer;
		private readonly string _untitledName;
		private string _savedText;
		private string _savedEncoding;
		private string? _language;

		public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(EditorDocument));
		public string Title { get => (string)GetValue(TitleProperty); private set => SetValue(TitleProperty, value); }
		public string? FilePath { get; private set; }
		public Encoding FileEncoding { get; private set; }
		public string? DiskHash { get; private set; }
		public FoldingManager Foldings => _folding;
		public bool IsDirty => Text != _savedText || EncodingId(FileEncoding) != _savedEncoding;
		public event Action<EditorDocument>? StateChanged;

		public string SyntaxLanguage
		{
			get => _language ?? "Text";
			set
			{
				_language = value;
				SyntaxHighlighting = HighlightingManager.Instance.GetDefinition(value == "Text" ? "" : value);
				UpdateFoldings();
				StateChanged?.Invoke(this);
			}
		}

		public EditorDocument(string name, string text = "", Encoding? encoding = null, string? path = null, string? diskHash = null)
		{
			_untitledName = name;
			FilePath = path;
			DiskHash = diskHash;
			FileEncoding = encoding ?? new UTF8Encoding(false, true);
			_savedEncoding = EncodingId(FileEncoding);
			_savedText = text;
			FontFamily = new FontFamily("Cascadia Mono, Consolas");
			FontSize = 14;
			Background = Brushes.White;
			ShowLineNumbers = true;
			HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto;
			VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto;
			Options.IndentationSize = 2;
			Options.ConvertTabsToSpaces = true;
			Options.HighlightCurrentLine = true;
			Text = text;
			Document.UndoStack.ClearAll();
			_folding = FoldingManager.Install(TextArea);
			_foldingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
			_foldingTimer.Tick += (_, _) => { _foldingTimer.Stop(); UpdateFoldings(); };
			TextChanged += (_, _) =>
			{
				UpdateTitle();
				_foldingTimer.Stop();
				_foldingTimer.Start();
				StateChanged?.Invoke(this);
			};
			TextArea.Caret.PositionChanged += (_, _) => StateChanged?.Invoke(this);
			SyntaxLanguage = GuessLanguage(path, text);
			UpdateTitle();
		}

		public void SetEncoding(Encoding encoding)
		{
			FileEncoding = encoding;
			UpdateTitle();
			StateChanged?.Invoke(this);
		}

		public void MarkSaved(string path, string hash)
		{
			FilePath = path;
			DiskHash = hash;
			_savedText = Text;
			_savedEncoding = EncodingId(FileEncoding);
			UpdateTitle();
			StateChanged?.Invoke(this);
		}

		private void UpdateTitle() => Title = (IsDirty ? "* " : "") + (FilePath == null ? _untitledName : Path.GetFileName(FilePath));

		public static string GuessLanguage(string? path, string text)
		{
			string extension = Path.GetExtension(path ?? "").ToLowerInvariant();
			if (extension is ".xml" or ".xsd" or ".xsl" or ".xslt" or ".svg" or ".config" or ".xaml")
			{
				return "XML";
			}

			if (extension == ".json")
			{
				return "JSON";
			}

			if (extension == ".cs")
			{
				return "C#";
			}

			if (extension is ".js" or ".mjs")
			{
				return "JavaScript";
			}

			if (extension == ".css")
			{
				return "CSS";
			}

			if (extension is ".htm" or ".html")
			{
				return "HTML";
			}

			string trimmed = text.TrimStart();
			return trimmed.StartsWith('<') ? "XML" : trimmed.StartsWith('{') || trimmed.StartsWith('[') ? "JSON" : "Text";
		}

		public void UpdateFoldings()
		{
			try
			{
				if (Text.Length > StructuredTextTools.MaximumCharacters)
				{
					_folding.UpdateFoldings([], -1);
					return;
				}
				if (SyntaxLanguage == "XML")
				{
					StructuredTextTools.ValidateXml(Text);
					new XmlFoldingStrategy().UpdateFoldings(_folding, Document);
				}
				else if (SyntaxLanguage == "JSON")
				{
					byte[] bytes = Encoding.UTF8.GetBytes(Text);
					var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = StructuredTextTools.MaximumDepth });
					var starts = new Stack<int>();
					var folds = new List<NewFolding>();
					int previousByte = 0;
					int characterOffset = 0;
					while (reader.Read())
					{
						int tokenByte = checked((int)reader.TokenStartIndex);
						characterOffset += Encoding.UTF8.GetCharCount(bytes.AsSpan(previousByte, tokenByte - previousByte));
						previousByte = tokenByte;
						if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
						{
							starts.Push(characterOffset);
						}
						else if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray)
						{
							int start = starts.Pop();
							if (Document.GetLineByOffset(start).LineNumber != Document.GetLineByOffset(characterOffset).LineNumber)
							{
								folds.Add(new NewFolding(start, characterOffset + 1));
							}
						}
					}
					_folding.UpdateFoldings(folds.OrderBy(item => item.StartOffset), -1);
				}
				else
				{
					_folding.UpdateFoldings([], -1);
				}
			}
			catch (Exception exception) when (exception is JsonException or System.Xml.XmlException or InvalidOperationException)
			{
				_folding.UpdateFoldings([], -1);
			}
		}

		private static string EncodingId(Encoding encoding) => encoding.CodePage + ":" + Convert.ToHexString(encoding.GetPreamble());

		public void Dispose()
		{
			_foldingTimer.Stop();
			FoldingManager.Uninstall(_folding);
		}
	}

	public sealed record NotepadFile(string Path, string Text, Encoding Encoding, string Hash)
	{
		public const int MaximumBytes = 20 * 1024 * 1024;

		public static NotepadFile Read(string path)
		{
			string fullPath = System.IO.Path.GetFullPath(path);
			using var input = File.OpenRead(fullPath);
			if (input.Length > MaximumBytes)
			{
				throw new InvalidDataException("Files larger than 20 MiB are not supported.");
			}

			using var memory = new MemoryStream();
			input.CopyTo(memory);
			byte[] bytes = memory.ToArray();
			Encoding encoding = new UTF8Encoding(false, true);
			int prefix = 0;
			if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 })) { encoding = new UTF32Encoding(false, true, true); prefix = 4; }
			else if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xFE, 0xFF })) { encoding = new UTF32Encoding(true, true, true); prefix = 4; }
			else if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) { encoding = new UTF8Encoding(true, true); prefix = 3; }
			else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) { encoding = new UnicodeEncoding(false, true, true); prefix = 2; }
			else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) { encoding = new UnicodeEncoding(true, true, true); prefix = 2; }
			string text = encoding.GetString(bytes, prefix, bytes.Length - prefix);
			if (text.Contains('\0'))
			{
				throw new InvalidDataException("Binary files are not supported.");
			}

			return new NotepadFile(fullPath, text, encoding, Convert.ToHexString(SHA256.HashData(bytes)));
		}

		public static string Save(string path, string text, Encoding encoding, bool overwrite = false)
		{
			string output = System.IO.Path.GetFullPath(path);
			if (!overwrite && File.Exists(output))
			{
				throw new IOException("The output file already exists.");
			}

			string temporary = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(output)!, $".Software.Notepad-{Guid.NewGuid():N}.tmp");
			try
			{
				using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
				using (var writer = new StreamWriter(stream, encoding))
				{
					writer.Write(text);
				}

				using var written = File.OpenRead(temporary);
				string hash = Convert.ToHexString(SHA256.HashData(written));
				written.Dispose();
				if (overwrite && File.Exists(output))
				{
					File.Replace(temporary, output, null);
				}
				else
				{
					File.Move(temporary, output);
				}

				return hash;
			}
			finally
			{
				if (File.Exists(temporary))
				{
					File.Delete(temporary);
				}
			}
		}
	}
}