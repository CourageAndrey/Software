# Merge Tool

A simple two-way WPF text diff/merge tool using DiffPlex and AvalonEdit. This is not a three-way conflict resolver: it compares two files and starts an editable result from the left file.

## Run

Requires the .NET 10 SDK.

```powershell
dotnet run --project Software.MergeTool
dotnet run --project Software.MergeTool -- 'C:\Folder\left.txt' 'C:\Folder\right.txt' 'C:\Folder\merged.txt'
```

The optional third argument chooses the result path. No file is written until Save result is clicked.

## Workflow

Choose left/right files and click Compare. The read-only source views align inserted/deleted lines, retain original line numbers, highlight changes, and synchronize vertical scrolling. Click a changed line or use the previous/next controls to select a difference.

Use left, Use right, or Use both replaces the selected block in the result. Use both keeps the left block before the right block. The result supports manual edits, selection, copy/paste, undo/redo, syntax highlighting, and Save/Save As. Edits outside a difference remain intact through anchored positions; replacing manual edits inside a difference asks for confirmation. Source files are not edited unless explicitly selected as the save destination.

Save preserves the left file's encoding by default, or can write UTF-8, UTF-8 with BOM, or UTF-16 LE. UTF-8 (with/without BOM) and BOM-marked UTF-16/UTF-32 sources are supported. Invalid UTF-8, binary files, files over 5 MiB, and files over 20,000 lines are rejected. Newline and whitespace changes are included in comparison. Selected source blocks retain their original line endings; Use both inserts LF only when a separator is needed between non-terminated blocks.

Result saves use a temporary file and atomic replacement. Overwriting existing output requires confirmation. Unsaved results prompt before closing or comparing new inputs.

Keyboard shortcuts: F7/Shift+F7 navigates differences; Ctrl+S saves; Ctrl+Shift+S saves as; standard editor shortcuts support copy/paste and undo/redo.

## Tests

```powershell
dotnet test Software.UnitTests --filter 'FullyQualifiedName~MergeEngineTests|FullyQualifiedName~MergeFileTests|FullyQualifiedName~MergeToolUiTests' -p:BuildInParallel=false
```

Tests cover exact block merging, varied newline/line edits, encoding round trips, safe output handling, aligned WPF views, merge buttons, undo/redo, and anchors surviving edits.