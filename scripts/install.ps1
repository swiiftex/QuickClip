<#
.SYNOPSIS
    Builds QuickClip and installs it for the current user.

.DESCRIPTION
    - Fetches FFmpeg/libmpv into ./deps if they're missing
    - Publishes a Release build to %LOCALAPPDATA%\Programs\QuickClip
    - Adds a Start menu shortcut
    - Adds "Edit with QuickClip" to Explorer's right-click menu for video/audio files:
        * on Windows 11's main menu through a small app package (needs Visual Studio's C++ tools to build).
          The package is signed with a certificate created on this PC; the first install asks once (UAC)
          to trust it.
        * otherwise in the classic menu ("Show more options")
#>
param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\QuickClip'),
    [switch]$NoContextMenu,
    [switch]$ClassicMenuOnly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$packageName = 'QuickClip.ContextMenu'
$publisher = 'CN=QuickClip Local' # must match packaging\AppxManifest.xml

function Install-MenuPackage {
    & (Join-Path $PSScriptRoot 'build-menu-package.ps1')
    $bin = Join-Path $root 'src\QuickClip.ShellExt\bin'
    $msix = Join-Path $bin 'QuickClip-menu.msix'

    # Unregister first so Explorer releases the old handler DLL.
    Get-AppxPackage -Name $packageName | Remove-AppxPackage
    $dll = Join-Path $Destination 'QuickClipShell.dll'
    Get-ChildItem $Destination -Filter 'QuickClipShell.dll.old*' | Remove-Item -Force -ErrorAction SilentlyContinue
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

    Add-AppxPackage -Path $msix -ExternalLocation $Destination
}

if (-not (Test-Path (Join-Path $root 'deps\libmpv-2.dll')) -or -not (Test-Path (Join-Path $root 'deps\ffmpeg\ffmpeg.exe'))) {
    & (Join-Path $PSScriptRoot 'get-deps.ps1')
}

$running = Get-Process QuickClip -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($Destination, 'OrdinalIgnoreCase') }
if ($running) { throw 'QuickClip is running from the install folder. Close it and try again.' }

Write-Host "Building QuickClip -> $Destination"
dotnet publish (Join-Path $root 'src\QuickClip\QuickClip.csproj') -c Release -r win-x64 --self-contained false -o $Destination -p:DebugType=none -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$exe = Join-Path $Destination 'QuickClip.exe'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'QuickClip.lnk'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $Destination
$link.Description = 'Trim, crop and export video and audio'
$link.Save()
Write-Host "Start menu shortcut -> $shortcut"

if (-not $NoContextMenu) {
    $mainMenu = $false
    if (-not $ClassicMenuOnly -and [Environment]::OSVersion.Version.Build -ge 22000) {
        try {
            Install-MenuPackage
            $mainMenu = $true
        } catch {
            Write-Warning "Couldn't add the Windows 11 main-menu entry: $($_.Exception.Message)"
            Write-Warning 'Falling back to the classic menu ("Show more options").'
        }
    }
    # Adds the classic entry, or removes it when the package already provides one.
    $p = Start-Process -FilePath $exe -ArgumentList '--register' -PassThru -Wait
    if ($p.ExitCode -ne 0) { throw 'Registering the Explorer menu entry failed (see %LOCALAPPDATA%\QuickClip\quickclip.log).' }
    if ($mainMenu) { Write-Host 'Added "Edit with QuickClip" to the Windows 11 right-click menu.' }
    else { Write-Host 'Added "Edit with QuickClip" to the classic right-click menu ("Show more options").' }
}

Write-Host 'Done.'
