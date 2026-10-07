# Text Processor

A WordPad-style WPF rich-text editor. Microsoft Word is not required.

## Run

Requires the .NET 10 SDK.

```powershell
dotnet run --project Software.TextProcessor
dotnet run --project Software.TextProcessor -- 'C:\Folder\document.docx'
```

Open RTF, TXT, DOC, or DOCX from the file menu or by dropping one file onto the window. The editor offers font/size selection, bold/italic/underline/strikethrough, colors, paragraph alignment, lists, indentation, images, basic tables, date/time insertion, find/replace, undo/redo, zoom, and printing.

## Document Formats

- RTF: native rich-text opening and saving through WPF.
- TXT: text opening and UTF-8 saving. Saving warns that formatting, tables, and images are lost.
- DOC: legacy Word 97-2003 text import through NPOI.HWPFCore. Character formatting, tables, images, and advanced layout are not preserved. DOC is import-only; save an edited copy as RTF or DOCX.
- DOCX: opening and saving through Open XML SDK. Common character formatting, paragraph alignment, simple lists/tables, and supported embedded images are retained. This is not a full Word layout engine: headers/footers, styles, tracked changes, equations, footnotes, fields, complex merged tables, and advanced list numbering are not faithfully retained. Import limitations are shown in the window.

Document file limit: 20 MiB. DOCX packages are capped at 10,000 entries and 100 MiB expanded size. Password-protected Word documents are not supported. External relationships are not fetched and macros are not executed.

Save uses a temporary file and atomic replacement. Unsaved changes prompt on new/open/close; saving a file changed externally asks before replacement. Printing uses a separate document copy and does not alter the editor layout.

Find/replace is literal, can be case-sensitive, wraps through matches, works across character-formatting runs within a paragraph, and does not match across paragraph boundaries. Replace All is grouped into one undo operation.

Keyboard shortcuts include Ctrl+N/O/S, Ctrl+Shift+S, Ctrl+P, Ctrl+F/H, F3, Escape to close search, and native rich-text formatting/editing shortcuts.

## Tests

```powershell
dotnet test Software.UnitTests --filter 'FullyQualifiedName~TextProcessorFileTests|FullyQualifiedName~TextProcessorUiTests' -p:BuildInParallel=false
```

Tests cover real binary DOC import, rich-text format round trips, valid generated DOCX structure, lists/tables/images, safe saving, and WPF formatting/search/file workflows. Physical printing and interactive dialogs are not exercised automatically.