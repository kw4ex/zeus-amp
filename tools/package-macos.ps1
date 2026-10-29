# SPDX-License-Identifier: GPL-2.0-or-later
#
# Builds the macOS install ZIP(s) for the KPA1500 feature.
#
#   pwsh tools/package-macos.ps1                 # both Apple silicon and Intel
#   pwsh tools/package-macos.ps1 -Rid osx-arm64  # one of them
#
# It runs the catalog's unmodified src/Kpa1500/build-package.ps1 with the
# macOS runtime selected (environment variable ZeusRid, read by the .csproj),
# then adds the native serial helper libSystem.IO.Ports.Native.dylib to the
# root of the ZIP, beside System.IO.Ports.dll, where .NET looks for it. The
# UDP network connection needs neither file; they serve the serial option.
#
# Output: artifacts/<id>/<id>-<version>-<rid>.zip (+ .sha256)

[CmdletBinding()]
param(
    [ValidateSet("osx-arm64", "osx-x64")]
    [string[]] $Rid = @("osx-arm64", "osx-x64")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$featureDir = Join-Path $repo "src/Kpa1500"
$manifest = Get-Content -Raw (Join-Path $featureDir "plugin.json") | ConvertFrom-Json
$id = [string]$manifest.id
$version = [string]$manifest.version
$outDir = Join-Path $repo "artifacts/$id"
$built = Join-Path $outDir "$id-$version.zip"
$binDir = Join-Path $featureDir "bin/Release/net10.0"
$nativeName = "libSystem.IO.Ports.Native.dylib"

foreach ($r in $Rid) {
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
