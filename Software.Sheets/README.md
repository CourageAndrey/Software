# Sheets

A simple Excel-like WPF spreadsheet editor powered by NPOI 2.7.5 (Apache-2.0). Microsoft Excel is not required. This version is deliberately pinned before later NPOI binary releases introduced additional maintenance-fee licensing terms.

## Run

Requires the .NET 10 SDK.

```powershell
dotnet run --project Software.Sheets
dotnet run --project Software.Sheets -- 'C:\Folder\workbook.xlsx'
```

Open XLS or XLSX from the file menu or by dropping one workbook on the window. Edit cells directly, or enter a value/formula in the formula bar and press Enter. Use the address box to jump to cells such as AB10. Formula input begins with `=`; use a leading apostrophe to force literal text. Numbers use invariant decimal notation, and TRUE/FALSE create boolean cells.

Worksheet tabs allow switching, adding, renaming, and deleting sheets. Undo/redo covers cell values, formulas, clipboard batches, and basic formatting. Worksheet structural changes reset cell history and require confirmation before deleting a sheet.

The grid has row/column virtualization, rectangular selection, copy/paste of quoted tab-delimited text, Delete to clear cells, bold/italic, alignment, and general/number/integer/percent/date/text display formats. Rows/Columns controls extend the visible area; the address box can also extend it. Formulas use NPOI's evaluator; common arithmetic, functions, ranges, and cross-sheet references are supported. Unsupported formulas remain stored and display `#UNSUPPORTED` when the evaluator cannot handle them. External workbooks are not opened automatically.

Save preserves the workbook's original XLS/XLSX format. New workbooks default to XLSX, with a separate New XLS command. Cross-format conversion is not supported. Atomic replacement, overwrite confirmation for Save As, external-change detection, and unsaved-change prompts protect file workflows.

## Limits

This is not a full Excel replacement. The grid does not render charts, pictures, merged ranges, conditional formatting, or advanced workbook features; preservation through NPOI is format-dependent and not guaranteed for those features. The original workbook model is retained rather than reconstructing sheets from displayed text. Macros are not executed. Protected/encrypted workbooks and XLSM are not supported.

Limits: 20 MiB files, 32 worksheets, 10,000 rows and 256 columns per worksheet, 250,000 stored cells per workbook; XLSX packages are capped at 100 MiB expanded and 10,000 entries. Clipboard text is capped at 2 Mi characters. Copying formulas keeps their references verbatim rather than adjusting them relatively. Basic pasted text is typed using the same input rules as cell editing. Number/date formatting changes display only; numeric dates use Excel serial values.

## Tests

```powershell
dotnet test Software.UnitTests --filter 'FullyQualifiedName~SheetsBookTests|FullyQualifiedName~SheetsUiTests' -p:BuildInParallel=false
```

Tests exercise actual XLS/XLSX round trips, formulas, styles, typed/literal values, sheet management, history, clipboard quoting, and real WPF grid/formula-bar/file workflows.