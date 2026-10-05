<#
.SYNOPSIS
    Removes QuickClip installed by install.ps1: the right-click menu entries, the menu package and its signing
    certificate, the Start menu shortcut and the program folder.
    Settings in %APPDATA%\QuickClip are kept unless -RemoveSettings is given.
#>
param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\QuickClip'),
    [switch]$RemoveSettings
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $Destination 'QuickClip.exe'
$publisher = 'CN=QuickClip Local'

# Windows 11 menu package, and the surrogate process that may still hold its handler DLL.
Get-AppxPackage -Name 'QuickClip.ContextMenu' | Remove-AppxPackage
Get-Process dllhost -ErrorAction SilentlyContinue |
    Where-Object { try { $_.Modules.ModuleName -contains 'QuickClipShell.dll' } catch { $false } } |
    Stop-Process -Force

# Classic menu entry
if (Test-Path $exe) {
    Start-Process -FilePath $exe -ArgumentList '--unregister' -Wait
} else {
    Remove-Item -LiteralPath 'HKCU:\Software\Classes\*\shell\QuickClip' -Recurse -Force -ErrorAction SilentlyContinue
    Get-ChildItem 'HKCU:\Software\Classes\SystemFileAssociations' -ErrorAction SilentlyContinue | ForEach-Object {
        $verb = Join-Path $_.PSPath 'shell\QuickClip'
        if (Test-Path $verb) { Remove-Item $verb -Recurse -Force }
    }
}

# Signing certificate: the trusted copy needs admin to remove.
$certs = @(Get-ChildItem Cert:\CurrentUser\My, Cert:\CurrentUser\CA | Where-Object Subject -eq $publisher)
$trusted = @(Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Subject -eq $publisher)
if ($trusted.Count -gt 0) {
    $remove = ($trusted | ForEach-Object { "Remove-Item 'Cert:\LocalMachine\TrustedPeople\$($_.Thumbprint)'" }) -join '; '
    Write-Host 'Windows will ask for permission to remove the QuickClip signing certificate.'
    try { Start-Process powershell -Verb RunAs -Wait -ArgumentList "-NoProfile -Command ""$remove""" }
    catch { Write-Warning 'The QuickClip certificate is still trusted; remove it from certlm.msc > Trusted People.' }
}
$certs | Remove-Item

$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'QuickClip.lnk'
if (Test-Path $shortcut) { Remove-Item $shortcut -Force }
if (Test-Path $Destination) { Remove-Item $Destination -Recurse -Force }
if ($RemoveSettings) {
    Remove-Item (Join-Path $env:APPDATA 'QuickClip') -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $env:LOCALAPPDATA 'QuickClip') -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host 'QuickClip removed.'
