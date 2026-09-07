# Publishes Sancho for all supported platforms (framework-dependent single-file).
# Output: artifacts\publish\<rid>\ per RID, plus release-ready assets staged in
# artifacts\release\ named per convention: sancho.exe (Windows), sancho-<os>-<arch>.
# Requires only the .NET 10 SDK - no AOT, no native toolchain.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

$rids = @("win-x64", "linux-x64", "linux-arm64", "osx-arm64", "osx-x64")

$releaseDir = Join-Path $root "artifacts\release"
New-Item -ItemType Directory -Force $releaseDir | Out-Null

foreach ($rid in $rids) {
    Write-Host "Publishing $rid..." -ForegroundColor Cyan

    dotnet publish (Join-Path $root "Sancho.Console\Sancho.Console.csproj") `
        -c Release `
        -r $rid `
        --self-contained false `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:InvariantGlobalization=true `
        -o (Join-Path $root "artifacts\publish\$rid")
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $rid (exit code $LASTEXITCODE)"
    }

    Remove-Item (Join-Path $root "artifacts\publish\$rid\*.pdb") -ErrorAction SilentlyContinue

    # Stage the release asset under its installer-facing name.
    $asset = if ($rid -eq "win-x64") { "sancho.exe" }
             else { "sancho-$rid" }
    $exeName = if ($rid -eq "win-x64") { "sancho.exe" } else { "sancho" }
    Copy-Item (Join-Path $root "artifacts\publish\$rid\$exeName") (Join-Path $releaseDir $asset) -Force
}

Write-Host ""
Write-Host "Published. Release assets staged in artifacts\release\:"
Get-ChildItem $releaseDir | ForEach-Object { Write-Host "  $($_.Name)" }
