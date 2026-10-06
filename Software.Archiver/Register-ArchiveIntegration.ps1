[CmdletBinding()]
param(
    [string]$ExecutablePath,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$classes = 'Software\Classes'
$registration = 'Software\Software.Archiver\ShellIntegration'
$programId = 'Software.Archiver.Archive'
$extensions = @('.zip', '.7z', '.rar', '.tar')
$verbs = @(
    "$classes\*\shell\Software.Archiver.Add",
    "$classes\Directory\shell\Software.Archiver.Add"
)
$extractVerbs = @($extensions | ForEach-Object { "$classes\SystemFileAssociations\$_\shell\Software.Archiver.Extract" })

function Set-RegistryDefault([string]$Path, [string]$Value) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($Path)
    try { $key.SetValue('', $Value, [Microsoft.Win32.RegistryValueKind]::String) }
    finally { $key.Dispose() }
}

function Remove-RegistryTree([string]$Path) {
    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($Path, $false)
}

if ($Uninstall) {
    foreach ($extension in $extensions) {
        $backup = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey("$registration\$extension")
        $association = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey("$classes\$extension", $true)
        try {
            if ($null -ne $association -and $association.GetValue('') -eq $programId -and $null -ne $backup) {
                if ($backup.GetValue('HadDefault', 0) -eq 1) {
                    $association.SetValue('', $backup.GetValue('PreviousDefault'), [Microsoft.Win32.RegistryValueKind]::String)
                } else {
                    $association.DeleteValue('', $false)
                }
            }
        } finally {
            if ($null -ne $association) { $association.Dispose() }
            if ($null -ne $backup) { $backup.Dispose() }
        }

        $openWith = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey("$classes\$extension\OpenWithProgids", $true)
        if ($null -ne $openWith) {
            try { $openWith.DeleteValue($programId, $false) }
            finally { $openWith.Dispose() }
        }
    }
    foreach ($verb in ($verbs + $extractVerbs)) { Remove-RegistryTree $verb }
    Remove-RegistryTree "$classes\$programId"
    $registeredApps = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\RegisteredApplications', $true)
    if ($null -ne $registeredApps) {
        try { $registeredApps.DeleteValue('Software.Archiver', $false) }
        finally { $registeredApps.Dispose() }
    }
    Remove-RegistryTree 'Software\Software.Archiver\Capabilities'
    Remove-RegistryTree $registration
    Write-Output 'Archiver integration removed for the current user. Other applications and protected UserChoice settings were not changed.'
} else {
    if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
        throw 'Specify -ExecutablePath pointing to Software.Archiver.exe in a built or published directory.'
    }
    $resolved = (Resolve-Path -LiteralPath $ExecutablePath).ProviderPath
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf) -or [IO.Path]::GetExtension($resolved) -ne '.exe') {
        throw 'ExecutablePath must point to an existing .exe file.'
    }
    $quotedExecutable = '"' + $resolved + '"'
    $openCommand = $quotedExecutable + ' --open "%1"'
    $addCommand = $quotedExecutable + ' --add "%1"'
    $extractCommand = $quotedExecutable + ' --extract "%1"'

    Set-RegistryDefault "$classes\$programId" 'Archiver archive'
    Set-RegistryDefault "$classes\$programId\DefaultIcon" ($quotedExecutable + ',0')
    Set-RegistryDefault "$classes\$programId\shell" 'open'
    Set-RegistryDefault "$classes\$programId\shell\open" 'Open in Archiver'
    Set-RegistryDefault "$classes\$programId\shell\open\command" $openCommand
    Set-RegistryDefault "$classes\$programId\shell\Software.Archiver.Extract" 'Extract archive...'
    Set-RegistryDefault "$classes\$programId\shell\Software.Archiver.Extract\command" $extractCommand

    foreach ($extension in $extensions) {
        $association = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$classes\$extension")
        $backup = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$registration\$extension")
        try {
            if ($null -eq $backup.GetValue('HadDefault')) {
                $prior = $association.GetValue('', $null)
                $backup.SetValue('HadDefault', [int]($null -ne $prior), [Microsoft.Win32.RegistryValueKind]::DWord)
                if ($null -ne $prior) { $backup.SetValue('PreviousDefault', [string]$prior) }
            }
            $association.SetValue('', $programId, [Microsoft.Win32.RegistryValueKind]::String)
        } finally {
            $association.Dispose()
            $backup.Dispose()
        }
        $openWith = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$classes\$extension\OpenWithProgids")
        try { $openWith.SetValue($programId, '', [Microsoft.Win32.RegistryValueKind]::String) }
        finally { $openWith.Dispose() }
    }

    foreach ($verb in $verbs) {
        Set-RegistryDefault $verb 'Add to archive...'
        Set-RegistryDefault "$verb\command" $addCommand
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($verb, $true)
        try {
            $key.SetValue('Icon', $quotedExecutable + ',0')
            $key.SetValue('MultiSelectModel', 'Single')
        } finally { $key.Dispose() }
    }

    foreach ($verb in $extractVerbs) {
        Set-RegistryDefault $verb 'Extract archive...'
        Set-RegistryDefault "$verb\command" $extractCommand
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($verb, $true)
        try {
            $key.SetValue('Icon', $quotedExecutable + ',0')
            $key.SetValue('MultiSelectModel', 'Single')
        } finally { $key.Dispose() }
    }

    $capabilitiesPath = 'Software\Software.Archiver\Capabilities'
    $capabilities = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($capabilitiesPath)
    try {
        $capabilities.SetValue('ApplicationName', 'Archiver')
        $capabilities.SetValue('ApplicationDescription', 'Browse and extract ZIP, 7z, RAR, and TAR archives.')
    } finally { $capabilities.Dispose() }
    $fileAssociations = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$capabilitiesPath\FileAssociations")
    try {
        foreach ($extension in $extensions) { $fileAssociations.SetValue($extension, $programId) }
    } finally { $fileAssociations.Dispose() }
    $registeredApps = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\RegisteredApplications')
    try { $registeredApps.SetValue('Software.Archiver', $capabilitiesPath) }
    finally { $registeredApps.Dispose() }

    Write-Output 'Archiver integration registered for the current user. If Windows has an existing protected default, choose Archiver in Settings > Apps > Default apps.'
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ArchiverShellNotification
{
    [DllImport("shell32.dll")]
    public static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
'@
[ArchiverShellNotification]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)