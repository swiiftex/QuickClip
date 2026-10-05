<#
.SYNOPSIS
    Builds a QuickClip release into .\publish: the installer (QuickClip-<version>-setup.exe) and a
    portable zip (QuickClip-<version>-win-x64.zip). The version comes from src\QuickClip\QuickClip.csproj.

.DESCRIPTION
    The app is published self-contained, so neither needs .NET installed. Needs the tools from
    scripts\get-deps.ps1 (FFmpeg, libmpv, Inno Setup) and Visual Studio's C++ tools for the recording engine.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $root 'src\QuickClip\QuickClip.csproj'
$iscc = Join-Path $root 'deps\innosetup\ISCC.exe'

$version = (Select-Xml -Path $csproj -XPath '//Version').Node.InnerText
if ($version -notmatch '^(\d+)\.(\d+)\.(\d+)(?:-(.+))?$') { throw "Unexpected version '$version' in QuickClip.csproj" }
$numeric = "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"
$display = "$($Matches[1]).$($Matches[2])" + $(if ([int]$Matches[3] -gt 0) { ".$($Matches[3])" } else { '' }) +
    $(if ($Matches[4]) { ' ' + $Matches[4].Replace('.', ' ') } else { '' })

if (-not (Test-Path $iscc) -or -not (Test-Path (Join-Path $root 'deps\ffmpeg\ffmpeg.exe'))) {
    & (Join-Path $PSScriptRoot 'get-deps.ps1')
}

$out = Join-Path $root 'publish'
$app = Join-Path $out 'QuickClip'
if (Test-Path $out) { Remove-Item -Recurse -Force $out }

Write-Host "Building QuickClip $version ($display)..."
dotnet publish $csproj -c Release -r win-x64 --self-contained true -o $app -p:DebugType=none -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
foreach ($required in 'QuickClip.exe', 'QuickClipCapture.dll', 'libmpv-2.dll', 'ffmpeg\ffmpeg.exe') {
    if (-not (Test-Path (Join-Path $app $required))) { throw "The build is missing $required." }
}

$zip = Join-Path $out "QuickClip-$version-win-x64.zip"
Write-Host 'Packing the portable zip...'
tar -a -c -f $zip -C $out QuickClip
if ($LASTEXITCODE -ne 0) { throw 'Creating the zip failed.' }

Write-Host 'Building the installer...'
& $iscc /Q "/DAppVersion=$version" "/DNumericVersion=$numeric" "/DDisplayVersion=$display" "/DSourceDir=$app" "/DOutputDir=$out" (Join-Path $root 'installer\QuickClip.iss')
if ($LASTEXITCODE -ne 0) { throw 'Building the installer failed.' }

Get-ChildItem $out -File | ForEach-Object {
    '{0}  {1,8:N1} MB  sha256 {2}' -f $_.Name, ($_.Length / 1MB), (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
