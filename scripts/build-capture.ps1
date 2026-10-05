<#
.SYNOPSIS
    Builds the native recording engine (QuickClipCapture.dll) into src\QuickClip.Capture\bin.

.DESCRIPTION
    Needs Visual Studio's C++ tools and the FFmpeg headers from scripts\get-deps.ps1 (deps\ffmpeg-dev).
    The QuickClip project runs this automatically when the engine sources change.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root 'src\QuickClip.Capture'
$out = Join-Path $src 'bin'
$ffdev = Join-Path $root 'deps\ffmpeg-dev'
if (-not (Test-Path (Join-Path $ffdev 'include\libavcodec\avcodec.h'))) { throw 'FFmpeg headers are missing; run scripts\get-deps.ps1.' }
New-Item -ItemType Directory -Force -Path $out | Out-Null

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = if (Test-Path $vswhere) { & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath }
if (-not $vs) { throw 'Visual Studio C++ build tools were not found (needed to build the recording engine).' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'

$sources = (Get-ChildItem $src -Filter *.cpp | ForEach-Object { "`"$($_.FullName)`"" }) -join ' '
$libs = 'avcodec.lib avformat.lib avfilter.lib avutil.lib dxgi.lib ole32.lib avrt.lib mmdevapi.lib winmm.lib'
$commands = @(
    "call `"$vcvars`" >nul 2>nul",
    "cl /nologo /O2 /EHsc /std:c++20 /MT /W4 /DUNICODE /D_UNICODE /external:I `"$ffdev\include`" /external:W0 /LD /Fo`"$out\\`" $sources /link /OUT:`"$out\QuickClipCapture.dll`" /IMPLIB:`"$out\QuickClipCapture.lib`" /LIBPATH:`"$ffdev\lib`" $libs"
) -join ' && '
cmd /c $commands
if ($LASTEXITCODE -ne 0) { throw 'Building the recording engine failed.' }
