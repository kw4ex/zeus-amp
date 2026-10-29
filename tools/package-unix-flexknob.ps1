# SPDX-License-Identifier: GPL-2.0-or-later
#
# Builds the macOS and Linux install ZIPs for the FlexControl Knob feature.
#
#   pwsh tools/package-unix-flexknob.ps1                   # all four
#   pwsh tools/package-unix-flexknob.ps1 -Rid linux-arm64  # just one (e.g. Raspberry Pi)
#
#   osx-arm64    Mac with Apple silicon (M1–M4)
#   osx-x64      Intel Mac
#   linux-x64    Linux PC (64-bit Intel/AMD)
#   linux-arm64  Raspberry Pi 4/5 and other 64-bit ARM Linux
#
# It runs the catalog's unmodified src/FlexKnob/build-package.ps1 with the
# runtime selected (environment variable ZeusRid, read by the .csproj), then
# adds the native serial helper (libSystem.IO.Ports.Native.dylib on macOS,
# .so on Linux) to the root of the ZIP, beside System.IO.Ports.dll, where .NET
# looks for it. The knob needs both files for its USB serial connection.
#
# Output: artifacts/<id>/<id>-<version>-<rid>.zip (+ .sha256)

[CmdletBinding()]
param(
    [ValidateSet("osx-arm64", "osx-x64", "linux-x64", "linux-arm64")]
    [string[]] $Rid = @("osx-arm64", "osx-x64", "linux-x64", "linux-arm64")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$featureDir = Join-Path $repo "src/FlexKnob"
$manifest = Get-Content -Raw (Join-Path $featureDir "plugin.json") | ConvertFrom-Json
$id = [string]$manifest.id
$version = [string]$manifest.version
$outDir = Join-Path $repo "artifacts/$id"
$built = Join-Path $outDir "$id-$version.zip"
$binDir = Join-Path $featureDir "bin/Release/net10.0"

foreach ($r in $Rid) {
    $nativeName = if ($r.StartsWith("osx")) { "libSystem.IO.Ports.Native.dylib" } else { "libSystem.IO.Ports.Native.so" }
    Write-Host "=== Building $id $version for $r"
    # Each runtime shares the same output folder, so start clean.
    foreach ($d in @("bin", "obj")) {
        $p = Join-Path $featureDir $d
        if (Test-Path $p) { Remove-Item -Recurse -Force $p }
    }
    $env:ZeusRid = $r
    try {
        & (Join-Path $featureDir "build-package.ps1") -ManagedDependency System.IO.Ports.dll
        if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "build-package.ps1 failed ($LASTEXITCODE)" }
    }
    finally { Remove-Item Env:ZeusRid -ErrorAction SilentlyContinue }

    $native = Join-Path $binDir $nativeName
    if (-not (Test-Path $native -PathType Leaf)) {
        throw "$nativeName was not produced for $r (looked in $binDir)."
    }

    $target = Join-Path $outDir "$id-$version-$r.zip"
    if (Test-Path $target) { Remove-Item -Force $target }
    Move-Item $built $target
    Remove-Item -Force -ErrorAction SilentlyContinue "$built.sha256"

    $zip = [IO.Compression.ZipFile]::Open($target, [IO.Compression.ZipArchiveMode]::Update)
    try {
        if ($zip.GetEntry($nativeName)) { throw "$nativeName is already in the package." }
        if (-not $zip.GetEntry("System.IO.Ports.dll")) { throw "System.IO.Ports.dll is missing from the package." }
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $zip, $native, $nativeName, [IO.Compression.CompressionLevel]::Optimal)
    }
    finally { $zip.Dispose() }

    $sha = (Get-FileHash -Algorithm SHA256 $target).Hash.ToLowerInvariant()
    Set-Content -NoNewline -Encoding ascii -Path "$target.sha256" -Value "$sha  $([IO.Path]::GetFileName($target))`n"
    Write-Host "Package: $target"
    Write-Host "SHA-256: $sha"
}
