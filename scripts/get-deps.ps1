<#
.SYNOPSIS
    Downloads the third-party binaries QuickClip needs into ./deps.

.DESCRIPTION
    - FFmpeg + FFprobe (BtbN shared GPL build, latest release branch) -> deps/ffmpeg/
    - libmpv (shinchiro build, used for the preview player)          -> deps/libmpv-2.dll

    The build copies everything in ./deps next to QuickClip.exe.
#>
param([switch]$Force)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is much faster without the progress bar

$root = Split-Path -Parent $PSScriptRoot
$deps = Join-Path $root 'deps'
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("quickclip-deps-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $deps, $tmp | Out-Null

function Get-ReleaseAssets([string]$repo) {
    (Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -Headers @{ 'User-Agent' = 'QuickClip' }).assets
}

function Save-Asset($asset) {
    $file = Join-Path $tmp $asset.name
    Write-Host ("Downloading {0} ({1:N0} MB)..." -f $asset.name, ($asset.size / 1MB))
    Invoke-WebRequest $asset.browser_download_url -OutFile $file -Headers @{ 'User-Agent' = 'QuickClip' }
    return $file
}

try {
    # --- FFmpeg ----------------------------------------------------------------
    $ffDir = Join-Path $deps 'ffmpeg'
    if ($Force -or -not (Test-Path (Join-Path $ffDir 'ffmpeg.exe'))) {
        $asset = Get-ReleaseAssets 'BtbN/FFmpeg-Builds' |
            Where-Object { $_.name -match '^ffmpeg-n(\d+\.\d+)-latest-win64-gpl-shared-[\d.]+\.zip$' } |
            Sort-Object { [version]($_.name -replace '^ffmpeg-n(\d+\.\d+)-.*$', '$1') } -Descending |
            Select-Object -First 1
        if (-not $asset) { throw 'Could not find an FFmpeg build to download.' }

        $zip = Save-Asset $asset
        $out = Join-Path $tmp 'ffmpeg'
        New-Item -ItemType Directory -Force -Path $out | Out-Null
        tar -xf $zip -C $out
        if ($LASTEXITCODE -ne 0) { throw 'Extracting FFmpeg failed.' }

        $bin = Get-ChildItem -Path $out -Recurse -Directory -Filter bin | Select-Object -First 1
        if (Test-Path $ffDir) { Remove-Item -Recurse -Force $ffDir }
        New-Item -ItemType Directory -Force -Path $ffDir | Out-Null
        Get-ChildItem $bin.FullName -File |
            Where-Object { $_.Name -ne 'ffplay.exe' } |
            Copy-Item -Destination $ffDir
        Copy-Item (Get-ChildItem -Path $out -Recurse -Filter LICENSE.txt | Select-Object -First 1).FullName (Join-Path $ffDir 'LICENSE.txt') -ErrorAction SilentlyContinue
        Write-Host "FFmpeg -> $ffDir"
    } else {
        Write-Host 'FFmpeg already present (use -Force to update).'
    }

    # --- libmpv ----------------------------------------------------------------
    $mpvDll = Join-Path $deps 'libmpv-2.dll'
    if ($Force -or -not (Test-Path $mpvDll)) {
        $asset = Get-ReleaseAssets 'shinchiro/mpv-winbuild-cmake' |
            Where-Object { $_.name -match '^mpv-dev-x86_64-\d{8}-git-.*\.7z$' } |
            Select-Object -First 1
        if (-not $asset) { throw 'Could not find a libmpv build to download.' }

        $archive = Save-Asset $asset
        $out = Join-Path $tmp 'mpv'
        New-Item -ItemType Directory -Force -Path $out | Out-Null
        tar -xf $archive -C $out
        if ($LASTEXITCODE -ne 0) {
            $7z = Join-Path $env:ProgramFiles '7-Zip\7z.exe'
            if (-not (Test-Path $7z)) { throw 'Extracting libmpv failed (needs Windows 11 tar or 7-Zip).' }
            & $7z x $archive "-o$out" -y | Out-Null
        }
        $dll = Get-ChildItem -Path $out -Recurse -Filter 'libmpv-2.dll' | Select-Object -First 1
        if (-not $dll) { throw 'libmpv-2.dll not found in the archive.' }
        Copy-Item $dll.FullName $mpvDll -Force
        Write-Host "libmpv -> $mpvDll"
    } else {
        Write-Host 'libmpv already present (use -Force to update).'
    }
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
