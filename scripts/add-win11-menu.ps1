<#
.SYNOPSIS
    Optional: puts "Edit with QuickClip" on Windows 11's main right-click menu (instead of only under
    "Show more options") for an installed QuickClip.

.DESCRIPTION
    Windows 11 only shows app-package entries on its main menu, so this builds a tiny package (a manifest plus
    QuickClipShell.dll) and signs it with a certificate created on this PC. Windows must trust that certificate,
    which asks for admin rights once (UAC). Needs Visual Studio's C++ tools to build the handler.

    -Remove takes the entry, the package and the certificate away again (the QuickClip uninstaller also removes
    the package).
#>
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\QuickClip'),
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$packageName = 'QuickClip.ContextMenu'
$publisher = 'CN=QuickClip Local' # must match packaging\AppxManifest.xml
$exe = Join-Path $InstallDir 'QuickClip.exe'
if (-not (Test-Path $exe)) { throw "QuickClip isn't installed in $InstallDir (install it with the setup first, or pass -InstallDir)." }

if ($Remove) {
    Get-AppxPackage -Name $packageName | Remove-AppxPackage
    $trusted = @(Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Subject -eq $publisher)
    if ($trusted.Count -gt 0) {
        $remove = ($trusted | ForEach-Object { "Remove-Item 'Cert:\LocalMachine\TrustedPeople\$($_.Thumbprint)'" }) -join '; '
        Write-Host 'Windows will ask for permission to remove the QuickClip signing certificate.'
        Start-Process powershell -Verb RunAs -Wait -ArgumentList "-NoProfile -Command ""$remove"""
    }
    Get-ChildItem Cert:\CurrentUser\My, Cert:\CurrentUser\CA | Where-Object Subject -eq $publisher | Remove-Item
    Start-Process -FilePath $exe -ArgumentList '--register' -Wait # back to the classic entry
    Write-Host 'Removed the Windows 11 menu entry; "Edit with QuickClip" is under "Show more options" again.'
    return
}

& (Join-Path $PSScriptRoot 'build-menu-package.ps1')
$bin = Join-Path $root 'src\QuickClip.ShellExt\bin'
$msix = Join-Path $bin 'QuickClip-menu.msix'

# Unregister first so Explorer releases the old handler DLL.
Get-AppxPackage -Name $packageName | Remove-AppxPackage
$dll = Join-Path $InstallDir 'QuickClipShell.dll'
Get-ChildItem $InstallDir -Filter 'QuickClipShell.dll.old*' | Remove-Item -Force -ErrorAction SilentlyContinue
if (Test-Path $dll) {
    try { Remove-Item $dll -Force } catch { Move-Item $dll "$dll.old$(Get-Random)" -Force } # still loaded: move it aside
}
Copy-Item (Join-Path $bin 'QuickClipShell.dll') $dll

# Signing certificate, private to this PC.
$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $publisher -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
    Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject $publisher -KeyUsage DigitalSignature `
        -FriendlyName 'QuickClip menu package signing (this PC only)' -CertStoreLocation Cert:\CurrentUser\My `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') -NotAfter (Get-Date).AddYears(10)
}
$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" | Sort-Object FullName | Select-Object -Last 1
if (-not $signtool) { throw 'signtool.exe (Windows SDK) was not found.' }
& $signtool.FullName sign /q /fd SHA256 /sha1 $cert.Thumbprint $msix
if ($LASTEXITCODE -ne 0) { throw 'Signing the menu package failed.' }

# Windows only installs packages whose certificate the machine trusts. Trusting it needs admin, once.
if (-not (Test-Path "Cert:\LocalMachine\TrustedPeople\$($cert.Thumbprint)")) {
    $cer = Join-Path $env:TEMP 'QuickClipLocal.cer'
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    Write-Host 'Windows will ask to let QuickClip trust its own signing certificate (one time only).'
    $import = "Import-Certificate -FilePath '$cer' -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null"
    Start-Process powershell -Verb RunAs -Wait -ArgumentList "-NoProfile -Command ""$import"""
    Remove-Item $cer -ErrorAction SilentlyContinue
    if (-not (Test-Path "Cert:\LocalMachine\TrustedPeople\$($cert.Thumbprint)")) { throw 'The certificate was not trusted.' }
}

Add-AppxPackage -Path $msix -ExternalLocation $InstallDir
Start-Process -FilePath $exe -ArgumentList '--register' -Wait # drops the classic entry, which would show twice
Write-Host 'Added "Edit with QuickClip" to the Windows 11 right-click menu.'
