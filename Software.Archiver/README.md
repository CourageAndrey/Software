# Archiver

A simple WPF archive manager for Windows. Creates ZIP archives and browses/extracts ZIP, 7z, and RAR using SharpCompress, with .NET's TAR reader for TAR archives. Creating 7z/RAR archives, updating existing archives, and encrypted ZIP creation are not supported.

## Run

Requires the .NET 10 SDK.

```powershell
dotnet run --project Software.Archiver
```

Use Open to browse archive entries, filter names, or extract all/selected entries. Selecting a folder includes its descendants. Enter an archive password before opening or extracting encrypted content; algorithm support depends on SharpCompress. New archive opens the ZIP creation view with file/folder inputs and compression choices. Drag files or folders onto that view to add them, or drop a supported archive on the contents view to open it.

Archive creation never overwrites an existing output and writes a temporary file before publishing the ZIP. Extraction refuses overwrites, unsafe paths, links, and transfers through filesystem junctions/symbolic links. Safety limits: 100,000 entries and 10 GiB of extracted content per operation. Cancellation/failure removes the current incomplete file but leaves previously extracted files and folders in place.

## Windows Integration

`Register-ArchiveIntegration.ps1` is provided separately and is not run by the app or build. It uses only the current user's registry, without administrator privileges.

After building or publishing to a stable directory, run manually:

```powershell
.\Software.Archiver\Register-ArchiveIntegration.ps1 -ExecutablePath 'R:\Software\Software.Archiver\bin\Debug\net10.0-windows\Software.Archiver.exe'
```

The script registers `.zip`, `.7z`, `.rar`, and `.tar`, adds Add to archive... for files/folders and Extract archive... for supported extensions, and lists Archiver in Open With/default-app registration. Windows may retain a protected UserChoice default; select Archiver in Settings > Apps > Default apps rather than modifying UserChoice. Windows 11 may place these classic verbs under Show more options. Each Explorer command operates on one selected filesystem item; add more inputs inside the app.

Uninstall manually:

```powershell
.\Software.Archiver\Register-ArchiveIntegration.ps1 -Uninstall
```

Uninstall removes Archiver's menu entries/registration and restores earlier per-user extension defaults only if they still point to Archiver. It does not change protected UserChoice settings.

Supported executable arguments:

```text
Software.Archiver.exe --open "C:\Folder\archive.zip"
Software.Archiver.exe --add "C:\Folder\file.txt" "C:\Another Folder"
Software.Archiver.exe --extract "C:\Folder\archive.7z"
```

An archive path alone opens it. Add opens the creation workflow; extract opens the archive, asks for a parent destination, and confirms extraction into an archive-named subfolder. No shell command immediately creates or extracts files without a user action or confirmation.

## Tests

```powershell
dotnet test Software.UnitTests --filter "FullyQualifiedName~ArchiveServiceTests|FullyQualifiedName~ArchiveLaunchTests|FullyQualifiedName~ArchiverUiTests"
```

Tests cover ZIP/TAR round trips, selected extraction, unsafe paths/links, conflicts, size limits, cancellation cleanup, shell arguments, and a real WPF archive view. Registry integration is intentionally not executed by tests.