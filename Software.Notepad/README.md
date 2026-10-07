# Notepad

A tabbed WPF text editor using AvalonEdit, with built-in XML/JSON tools inspired by Notepad++'s XML Tools and JsonTools. It does not load Notepad++ plugins or offer their entire feature sets.

## Run

Requires the .NET 10 SDK.

```powershell
dotnet run --project Software.Notepad
dotnet run --project Software.Notepad -- 'C:\Folder\file.xml' 'C:\Folder\data.json'
```

Open multiple files or drop files onto the window. Tabs retain text, caret position, folding, and undo history. Save, Save As, and Save All preserve the selected encoding and write through a temporary file. Saving a file changed externally asks before overwriting. Closing edited tabs or the window offers Save/Discard/Cancel.

The editor has line numbers, syntax highlighting, XML/JSON folding, word wrap, whitespace display, font-size adjustment, and line-ending conversion. Supported highlighting modes include text, XML, JSON, C#, JavaScript, HTML, and CSS. Encodings: strict UTF-8 with/without BOM and BOM-marked UTF-16/UTF-32; UTF-8 and UTF-16 can also be chosen explicitly. Binary files, legacy ANSI encodings, and files over 20 MiB are not supported.

## XML Tools

- Pretty print, compact, and validate XML; format the selection if one is present.
- Validate against a chosen XSD. External schema resolution and DTD/entity processing are disabled.
- Inspect an element/attribute tree and evaluate XPath expressions, including scalar expressions. Namespace declarations on the root are available to XPath; the default namespace uses prefix `d`.
- Preserve mixed content, `xml:space`, comments, CDATA, and the XML declaration during formatting.

## JSON Tools

- Pretty print, compact, validate, and recursively sort object keys without sorting arrays.
- Inspect a JSON tree and evaluate RFC 9535 JSONPath expressions through JsonPath.Net, including filters and array selection.
- Preserve numeric precision during formatting. JSON is strict: comments, trailing commas, and duplicate object keys are rejected.

The tree refreshes on request; clicking a node copies its query path into the query field. Tool diagnostics/query results appear in the sidebar and never replace the editor text. Formatting is undoable. A malformed document is left unchanged.

Structured tools support at most 2 Mi characters, 128 nesting levels, 10,000 tree/query nodes, and 2 Mi characters of query output. Invalid or overly large data shows a diagnostic. Formatting XML whitespace can be semantically significant in element-only content; mixed content and explicit `xml:space="preserve"` are retained. Changing the file encoding does not rewrite XML encoding declarations automatically.

## Search

Find/replace supports match case, whole words, regular expressions, wrapped searches, and undoable Replace All. Regex replacements support capture groups; literal replacements treat dollar signs literally. Regex evaluation has a 250 ms timeout.

Shortcuts: Ctrl+N/O/S, Ctrl+Shift+S, Ctrl+W, Ctrl+Tab/Shift+Ctrl+Tab, Ctrl+F/H, F3, Escape to close search, and standard editor undo/redo/copy/paste shortcuts.

## Tests

```powershell
dotnet test Software.UnitTests --filter 'FullyQualifiedName~NotepadToolsTests|FullyQualifiedName~NotepadSearchTests|FullyQualifiedName~NotepadFileTests|FullyQualifiedName~NotepadUiTests' -p:BuildInParallel=false
```

Tests cover structured formatting/queries/validation, safe parsing, precise JSON numbers, search, encoding/atomic-save behavior, and real WPF tabs, formatting, folding, undo, and file workflows.