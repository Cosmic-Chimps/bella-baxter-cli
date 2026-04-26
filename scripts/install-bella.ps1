# Bella CLI installer for Windows (PowerShell)
# Usage: irm https://raw.githubusercontent.com/cosmic-chimps/bella-baxter-cli/main/scripts/install-bella.ps1 | iex
# Or with a specific version:
#   $env:BELLA_VERSION="1.2.3"; irm ... | iex
# Or to install to a custom directory:
#   $env:BELLA_INSTALL_DIR="C:\tools"; irm ... | iex

param(
    [string]$Version = $env:BELLA_VERSION,
    [string]$InstallDir = $env:BELLA_INSTALL_DIR
)

$ErrorActionPreference = "Stop"

$Repo = "cosmic-chimps/bella-baxter-cli"
$BinaryName = "bella.exe"

# Determine install directory
if (-not $InstallDir) {
    $InstallDir = Join-Path $env:LOCALAPPDATA "Programs\bella"
}

function Write-Info    { param($Msg) Write-Host "[bella] $Msg" -ForegroundColor Cyan }
function Write-Success { param($Msg) Write-Host "[bella] $Msg" -ForegroundColor Green }
function Write-Warn    { param($Msg) Write-Host "[bella] $Msg" -ForegroundColor Yellow }
function Write-Err     { param($Msg) Write-Host "[bella] ERROR: $Msg" -ForegroundColor Red; exit 1 }

# Detect architecture
function Get-Arch {
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
    switch ($arch) {
        "X64"   { return "x64" }
        "Arm64" { return "arm64" }
        default { Write-Err "Unsupported architecture: $arch" }
    }
}

# Get latest version from GitHub
function Get-LatestVersion {
    try {
        $response = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -ErrorAction Stop
        return $response.tag_name -replace '^v', ''
    } catch {
        Write-Err "Failed to fetch latest version from GitHub: $_"
    }
}

# Extract the expected SHA256 hash for a given filename from checksums.txt
function Get-ExpectedHash {
    param([string]$ChecksumFile, [string]$AssetName)
    $line = Get-Content $ChecksumFile | Where-Object { $_ -match "^\S+\s+$([regex]::Escape($AssetName))$" }
    if (-not $line) { return $null }
    return ($line -split '\s+')[0].Trim()
}

# Add directory to user PATH (persistent, no reboot needed)
function Add-ToUserPath {
    param($Dir)
    $currentPath = [Environment]::GetEnvironmentVariable("PATH", "User")
    if ($currentPath -notlike "*$Dir*") {
        $newPath = "$Dir;$currentPath"
        [Environment]::SetEnvironmentVariable("PATH", $newPath, "User")
        $env:PATH = "$Dir;$env:PATH"
        Write-Info "Added $Dir to your user PATH."
    }
}

# Main
Write-Info "Installing Bella CLI..."

$arch = Get-Arch
Write-Info "Detected architecture: $arch"

if (-not $Version) {
    Write-Info "Fetching latest release..."
    $Version = Get-LatestVersion
}
Write-Info "Installing version: $Version"

# Asset name matches filenames published by the release workflow
$assetName = "cli-win-$arch.exe"
$baseUrl   = "https://github.com/$Repo/releases/download/v$Version"
$downloadUrl  = "$baseUrl/$assetName"
$checksumsUrl = "$baseUrl/checksums.txt"

# Temp paths
$tmpFile      = [System.IO.Path]::GetTempFileName() + ".exe"
$tmpChecksums = [System.IO.Path]::GetTempFileName()

try {
    # Download checksums.txt first (required)
    Write-Info "Downloading checksums.txt ..."
    Invoke-WebRequest -Uri $checksumsUrl -OutFile $tmpChecksums -UseBasicParsing

    # Download binary
    Write-Info "Downloading $assetName ..."
    Invoke-WebRequest -Uri $downloadUrl -OutFile $tmpFile -UseBasicParsing

    # Verify SHA256
    Write-Info "Verifying SHA256 checksum..."
    $expectedHash = Get-ExpectedHash -ChecksumFile $tmpChecksums -AssetName $assetName
    if (-not $expectedHash) {
        Write-Err "Checksum for '$assetName' not found in checksums.txt — cannot verify integrity."
    }
    $actualHash = (Get-FileHash $tmpFile -Algorithm SHA256).Hash.ToLower()
    if ($expectedHash.ToLower() -ne $actualHash) {
        Write-Err "Checksum verification FAILED!`n  Expected: $expectedHash`n  Got:      $actualHash`n  The download may be corrupted or tampered with. Aborting."
    }
    Write-Success "Checksum verified ✓"

    # Verify GPG signature (optional — skipped gracefully if gpg not installed or key unavailable)
    $sigUrl     = "$baseUrl/checksums.txt.asc"
    $tmpSig     = [System.IO.Path]::GetTempFileName()
    $tmpPubKey  = [System.IO.Path]::GetTempFileName()
    $SigningFingerprint = "65BB8D3CEEE3DD9E4FFD22B4119F114CA309C2FA"
    $skipGpg    = $env:BELLA_SKIP_GPG -eq "1"

    if (-not $skipGpg -and (Get-Command gpg -ErrorAction SilentlyContinue)) {
        try {
            Invoke-WebRequest -Uri $sigUrl -OutFile $tmpSig -UseBasicParsing -ErrorAction Stop
            if ((Get-Item $tmpSig).Length -gt 0) {
                Write-Info "Verifying GPG signature..."
                # Fetch public key from keyserver
                try {
                    Invoke-WebRequest -Uri "https://keys.openpgp.org/vks/v1/by-fingerprint/$SigningFingerprint" `
                        -OutFile $tmpPubKey -UseBasicParsing -ErrorAction Stop
                    & gpg --batch --import $tmpPubKey 2>$null
                    $gpgResult = & gpg --batch --verify $tmpSig $tmpChecksums 2>&1
                    if ($LASTEXITCODE -eq 0) {
                        Write-Success "GPG signature verified ✓"
                    } else {
                        Write-Err "GPG signature verification FAILED!`n  The checksums file does not match the expected signing key.`n  Set `$env:BELLA_SKIP_GPG=1 to bypass (not recommended)."
                    }
                } catch {
                    Write-Warn "Could not fetch signing public key — GPG signature check skipped."
                    Write-Warn "(SHA256 checksum was verified successfully.)"
                }
            }
        } catch {
            Write-Warn "No GPG signature published for this release — skipping signature check."
        } finally {
            if (Test-Path $tmpSig)    { Remove-Item $tmpSig    -Force -ErrorAction SilentlyContinue }
            if (Test-Path $tmpPubKey) { Remove-Item $tmpPubKey -Force -ErrorAction SilentlyContinue }
        }
    }

    # Smoke test
    $versionOutput = & $tmpFile --version 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Err "Downloaded binary failed to execute. Please report at https://github.com/$Repo/issues"
    }

    # Ensure install directory exists
    if (-not (Test-Path $InstallDir)) {
        New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    }

    $installPath = Join-Path $InstallDir $BinaryName

    # Move binary (replace if exists)
    Move-Item -Path $tmpFile -Destination $installPath -Force

    Write-Success "Bella CLI $Version installed to $installPath"

    # Add to PATH
    Add-ToUserPath $InstallDir

    # Verify
    $cmd = Get-Command bella -ErrorAction SilentlyContinue
    if ($cmd) {
        Write-Success "Run 'bella --help' to get started!"
    } else {
        Write-Warn ""
        Write-Warn "Restart your terminal or run the following to use bella in this session:"
        Write-Warn "  `$env:PATH = `"$InstallDir;`$env:PATH`""
        Write-Warn ""
    }
} finally {
    if (Test-Path $tmpFile)      { Remove-Item $tmpFile      -Force -ErrorAction SilentlyContinue }
    if (Test-Path $tmpChecksums) { Remove-Item $tmpChecksums -Force -ErrorAction SilentlyContinue }
}
