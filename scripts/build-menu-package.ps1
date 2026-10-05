<#
.SYNOPSIS
    Builds the Windows 11 context menu handler (QuickClipShell.dll) and the sparse package (QuickClip-menu.msix).

.DESCRIPTION
    Needs Visual Studio's C++ tools and the Windows SDK. Output goes to src\QuickClip.ShellExt\bin.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root 'src\QuickClip.ShellExt'
$out = Join-Path $src 'bin'
New-Item -ItemType Directory -Force -Path $out | Out-Null

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = if (Test-Path $vswhere) { & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath }
if (-not $vs) { throw 'Visual Studio C++ build tools were not found (needed for the Windows 11 menu entry).' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'

$commands = @(
    "call `"$vcvars`" >nul 2>nul",
    "cd /d `"$src`"",
    "rc /nologo /fo `"$out\ShellExt.res`" ShellExt.rc",
    "cl /nologo /O2 /EHsc /std:c++17 /W4 /DUNICODE /D_UNICODE /LD /Fo`"$out\\`" ShellExt.cpp `"$out\ShellExt.res`" /link /DEF:ShellExt.def /OUT:`"$out\QuickClipShell.dll`" /IMPLIB:`"$out\ShellExt.lib`" shlwapi.lib shell32.lib ole32.lib runtimeobject.lib",
    "makeappx pack /o /nv /d `"$root\packaging`" /p `"$out\QuickClip-menu.msix`""
) -join ' && '
cmd /c $commands
if ($LASTEXITCODE -ne 0) { throw 'Building the Windows 11 menu entry failed.' }
Write-Host "Menu handler -> $out"
