# File Commander

A simple two-pane WPF file manager for Windows.

## Run

Requires the .NET 10 SDK.

```powershell
dotnet run --project Software.FileCommander
```

Both panes start in your user folder. Each pane has a drive selector, editable folder path, parent-folder and refresh buttons, and a sortable file list. Enter a path and press Enter, or double-click a folder. Files open in their default Windows application. Ctrl/Shift selection supports multiple items. Drag the middle divider to resize the panes.

The focused pane is the source; copy and move use the other pane as the destination. F2 renames, F5 copies, F6 moves, F7 creates a folder in the active pane, and F8/Delete deletes. Enter opens an item, Backspace goes to its parent folder, and Tab switches between file lists.

Transfers include folder contents and never overwrite existing items. Known conflicts are checked before starting a batch. Permission, device, or other errors during execution can still leave a partially completed batch. Symbolic links, junctions, and directory transfers through them are not supported.

Delete requests the Windows Recycle Bin and uses Windows confirmation dialogs. Windows may offer permanent deletion for locations that cannot be recycled, such as network shares. Operations run in the background; closing the window is blocked until they finish.

## Tests

```powershell
dotnet test Software.UnitTests --filter "FullyQualifiedName~FileOperationTests|FullyQualifiedName~FileCommanderUiTests"
```

Tests use temporary folders for transfers, conflict handling, rename, folder creation, and pane navigation. The UI smoke test opens a window. Recycle Bin deletion and launching external file associations are not exercised automatically.