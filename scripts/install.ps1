# Installs Sancho for all users on a Windows machine:
#   1. Installs the .NET 10 SDK if dotnet is not already present
#   2. Downloads the latest sancho.exe from GitHub releases
#   3. Copies it to %ProgramFiles%\Sancho and adds it to the machine PATH
#
# One-liner usage:
#   irm https://raw.githubusercontent.com/PavlovicDzFilip/Sancho/master/scripts/install.ps1 | iex
# For macOS/Linux use scripts/install.sh.
#
# Requires administrator privileges (auto-elevates via UAC).
$ErrorActionPreference = "Stop"

$RepoOwner = "PavlovicDzFilip"
$RepoName = "Sancho"
$RawBase = "https://raw.githubusercontent.com/$RepoOwner/$RepoName/master"
$ReleaseBase = "https://github.com/$RepoOwner/$RepoName/releases/latest/download"

# Enable TLS 1.2 for PowerShell 5.1, which defaults to TLS 1.0.
try {
    [Net.ServicePointManager]::SecurityProtocol =
        [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
} catch { }

function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# ---- Elevate if needed ---------------------------------------------------
if (-not (Test-Admin)) {
    Write-Host "Requesting administrator privileges..." -ForegroundColor Yellow
    $shell = if (Get-Command pwsh -ErrorAction SilentlyContinue) { (Get-Command pwsh).Source }
             else { (Get-Command powershell).Source }

    # When run via "irm ... | iex" there is no script file, so fetch a copy
    # to a temp file first; -File invocation can relaunch itself directly.
    $scriptPath = if ($PSCommandPath) { $PSCommandPath }
                  else {
                      $tmp = Join-Path $env:TEMP "sancho-install.ps1"
                      Invoke-WebRequest -UseBasicParsing "$RawBase/scripts/install.ps1" -OutFile $tmp
                      $tmp
                  }

    Start-Process $shell -Verb RunAs -Wait `
        -ArgumentList "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$scriptPath`""
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Install finished. Open a new terminal and run: sancho --help"
    } else {
        Write-Host "Install failed (exit code $LASTEXITCODE)." -ForegroundColor Red
    }
    exit $LASTEXITCODE
}

# ---- Install the .NET SDK if missing --------------------------------------
# Probe by running dotnet and checking the version, not by PATH lookup alone:
# a broken stub or a non-10.x install would pass an existence check.
function Test-Dotnet10 {
    try {
        $v = dotnet --version 2>$null
        return $null -ne $v -and $v.Trim() -match '^10\.'
    } catch {
        return $false
    }
}

if (-not (Test-Dotnet10)) {
    Write-Host "dotnet 10 not available - installing the .NET 10 SDK..." -ForegroundColor Yellow

    $ok = $false
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        winget install --id Microsoft.DotNet.SDK.10 -e `
            --silent --accept-package-agreements --accept-source-agreements
        $ok = $LASTEXITCODE -eq 0
    }

    if (-not $ok) {
        Write-Host "winget unavailable or failed - using the official dotnet-install script..."
        $dotnetInstallDir = Join-Path $env:ProgramFiles "dotnet"
        $installScript = Join-Path $env:TEMP "dotnet-install.ps1"
        Invoke-WebRequest -UseBasicParsing "https://dot.net/v1/dotnet-install.ps1" -OutFile $installScript
        & $installScript -Channel 10.0 -InstallDir $dotnetInstallDir
        if ($LASTEXITCODE -ne 0) { throw "dotnet-install.ps1 failed (exit code $LASTEXITCODE)" }

        $machinePath = [Environment]::GetEnvironmentVariable("Path", "Machine")
        if ($null -eq $machinePath) { $machinePath = "" }
        if ($machinePath.Split(';') -notcontains $dotnetInstallDir) {
            [Environment]::SetEnvironmentVariable("Path", ($machinePath.TrimEnd(';') + ";" + $dotnetInstallDir), "Machine")
        }
    }

    # Pick up the freshly installed dotnet in this session.
    $env:Path = [Environment]::GetEnvironmentVariable("Path", "Machine") + ";" +
                [Environment]::GetEnvironmentVariable("Path", "User")
    if (-not (Test-Dotnet10)) {
        throw ".NET SDK installation reported success, but a working dotnet 10 is still not on PATH."
    }
} else {
    Write-Host "dotnet already installed: $((dotnet --version).Trim())"
}

# ---- Download the published executable ------------------------------------
Write-Host "Downloading sancho.exe..."
$tmpExe = Join-Path $env:TEMP "sancho.exe"
try {
    Invoke-WebRequest -UseBasicParsing "$ReleaseBase/sancho.exe" -OutFile $tmpExe
} catch {
    throw "Could not download sancho.exe from $ReleaseBase - no GitHub release exists yet.`n" +
          "Publish with scripts\publish.ps1 and attach artifacts\publish\win-x64\sancho.exe to a release."
}

# ---- Install for all users -------------------------------------------------
$programFiles = $env:ProgramW6432
if (-not $programFiles) { $programFiles = $env:ProgramFiles }
$installDir = Join-Path $programFiles "Sancho"
New-Item -ItemType Directory -Force $installDir | Out-Null

try {
    Copy-Item $tmpExe (Join-Path $installDir "sancho.exe") -Force
} catch {
    throw "Could not write to $installDir. Close any running sancho.exe and try again."
}

$machinePath = [Environment]::GetEnvironmentVariable("Path", "Machine")
if ($null -eq $machinePath) { $machinePath = "" }
if ($machinePath.Split(';') -notcontains $installDir) {
    [Environment]::SetEnvironmentVariable("Path", ($machinePath.TrimEnd(';') + ";" + $installDir), "Machine")
}

Remove-Item $tmpExe -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Sancho installed to $installDir and added to the machine PATH." -ForegroundColor Green
Write-Host "Open a new terminal and run:  sancho --help"
