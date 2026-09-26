# Bella CLI installer for Windows (PowerShell)
#
# Usage (the script is a release asset, so it is served from a release, never from a branch):
#   irm https://github.com/Cosmic-Chimps/bella-baxter-cli/releases/latest/download/install-bella.ps1 | iex
# A specific release — the script served by release vX.Y.Z installs vX.Y.Z by default:
#   irm https://github.com/Cosmic-Chimps/bella-baxter-cli/releases/download/vX.Y.Z/install-bella.ps1 | iex
# Or pick the version explicitly:
#   $env:BELLA_VERSION="1.2.3"; irm ... | iex
# Or to install to a custom directory:
#   $env:BELLA_INSTALL_DIR="C:\tools"; irm ... | iex
#
# Every install verifies, and ABORTS on any failure (#828):
#   1. the GPG signature over the release's checksums.txt, against the Cosmic Chimps release key
#      embedded below and pinned by fingerprint — this is what proves the release is ours;
#   2. the binary's SHA-256 against that (now authenticated) checksums.txt.
# A missing signature, a missing gpg, a key that will not import or a signature that does not
# verify each stop the install. The single opt-out, for air-gapped mirrors that cannot carry the
# signature, is $env:BELLA_INSECURE_SKIP_SIGNATURE="1". It skips step 1 only, loudly; step 2 still runs.
# gpg ships with Git for Windows (and Gpg4win); this script finds either even when it is not on PATH.

param(
    [string]$Version = $env:BELLA_VERSION,
    [string]$InstallDir = $env:BELLA_INSTALL_DIR
)

$ErrorActionPreference = "Stop"

$Repo = "Cosmic-Chimps/bella-baxter-cli"
$BinaryName = "bella.exe"

# Stamped by the release workflow (publish.yml) with the version of the release this copy is an asset
# of, so `releases/download/vX/install-bella.ps1` installs vX. An unstamped copy keeps the placeholder
# and falls back to the latest release.
$ReleaseVersion = '@BELLA_RELEASE_VERSION@'
if ($ReleaseVersion -like '@*@') { $ReleaseVersion = '' }

# The Cosmic Chimps release-signing key. The FINGERPRINT is the trust anchor: a signature is accepted
# only when gpg reports it VALID and made by this exact primary key. The key is embedded so
# verification needs no keyserver; it is the same key as scripts/bella-signing-key.asc and the
# `bella-signing-key.asc` release asset, and publish.yml refuses to release with any other key.
$SigningFingerprint = "65BB8D3CEEE3DD9E4FFD22B4119F114CA309C2FA"
$FirstSignedRelease = "0.1.1-preview.26"
$SigningKey = @'
-----BEGIN PGP PUBLIC KEY BLOCK-----
Comment: 65BB 8D3C EEE3 DD9E 4FFD  22B4 119F 114C A309 C2FA
Comment: Cosmic Chimps <it@cosmic-chimps.com>

xsFNBGnFpIYBEADWbCNcVK58NxqJjbYHTJrslRpvim70WMLl+l8MIc0afxqRMhrb
oijgYSyVY3pKpv4CBTaBLj+F4RYs6G9VuSMzJth3mTeKArHloOzjdjuZXVA5YEyz
V6MgTKdjhd+vYP87LsJw+h+ceO7Jpq1j+PK1SosA+bRDun4jg1U2pBUNN8arEs+3
l4kug/pJaP1fBmAz77izg3z2lV0wYGAYVY/VS3rZ+SwZW6TWlzv7ddt6tfmcNGtY
1HVBwo78DI78zj5xFMMvQOYGKgWpuzyfMfBxNlQu+q3REOyM1P1yzvymvWY/mgHw
Vi7Y1jTm5acu3JSNb4bC7C0Tvo6sLEp8WjojdXBTg56deippTd4L9lcO78QzXIxk
uZ5kSQTuo8sySpG5qp4zZy+19sck6Kpu+fA8jtjbAie5sPYonqNoeVSqgwfDMaF7
zQN2ixNzUAG7MWuG+qz1/5a5b1Imj+dxNa2rH9nA/F23ucoigKjLllonuZDR8eSb
ahq5ws0ScFstKw2afIKrztv26QmUGz5p0f8O52Bu/ttRXXOs70MR/qAROy4OU92u
GXj82n4FxFvRaZ9fG7lTaB0r+4ePSB10OmB29k3wURhbsm4qKqVqwgAlXBHUSDIF
PiyTIi8ovGe+7iQcD1Xcq8p3hgDtt1H9iYfXDGKzs5rw3+ZWFZR/pniIfQARAQAB
zSRDb3NtaWMgQ2hpbXBzIDxpdEBjb3NtaWMtY2hpbXBzLmNvbT7CwY4EEwEIADgW
IQRlu4087uPdnk/9IrQRnxFMownC+gUCacWkhgIbLwULCQgHAgYVCgkICwIEFgID
AQIeAQIXgAAKCRARnxFMownC+gSuEAC8j2nHUtZXKVD8elpgIozcnzJ4NsVMJMjB
avAlKxSDJ3PqP3NdlCPZ4QEwGknMisUD9hPUo7JYm7ayMxtea18xVxZ72JnYUfpd
nqA0kPAKNuLeY0jQsKUyECQQ6sfLGve11c6+K1yxKDY3u3hIwDYaKG7U5UnTKINY
jD8d3ZCZdS7Pb22NuqiNFsSLz/0DWiAS4IPgChJPHAWNZsnJP6R/KLZCKNkh+Ne4
XkVuEO/lgAl2Hmkxrwhh17QyNxoTEYssufXcKGGjhTo3tdTlz+4GQkrg26gYGxII
gHpXMFw1ehXjvceGyrwB+BUjLmwgpH3nRK4gMHuUrMvJNvP+6bTMlULTrUT9O7dA
RT6XSF4fuvmGQ5aHVsWiDAWSx4bAtNrahMnY7Kutl75bHRneAiCQ+uQXM16+xgOJ
h8oiGlkLvEayMwWHEWC3ZqmRtHCsU3Q38RBN8qkykpraPr0+8u75M90PM8Mg4PSs
vQY+dAGvaBmRF/ZWQojjBUQPxFddtGGtbNN+LOvAsv4h9dg2EmqKBZ2YMQFiAoRm
qVT8k75esOst6iCWsXzttbHpfbotFCenbkmBzYSL4zPH8rLkTyUYToiiXC+IKCGW
eQ5obHQ35H9yTxdBN/knB5CCmAMjfb6BCTqyWYbemC3drzEvnrM+E3hxh2SLsstL
kq+DsXQGlw==
=+0iq
-----END PGP PUBLIC KEY BLOCK-----
'@

# Determine install directory
if (-not $InstallDir) {
    $InstallDir = Join-Path $env:LOCALAPPDATA "Programs\bella"
}

function Write-Info    { param($Msg) Write-Host "[bella] $Msg" -ForegroundColor Cyan }
function Write-Success { param($Msg) Write-Host "[bella] $Msg" -ForegroundColor Green }
function Write-Warn    { param($Msg) Write-Host "[bella] $Msg" -ForegroundColor Yellow }
function Write-Err     { param($Msg) Write-Host "[bella] ERROR: $Msg" -ForegroundColor Red; exit 1 }

$RefusalHint = "  If you cannot verify signatures (an air-gapped mirror without the .asc, say), you may`n" +
               "  set `$env:BELLA_INSECURE_SKIP_SIGNATURE=`"1`" to install on the SHA-256 checksum alone.`n" +
               "  That proves the download is intact, NOT that Cosmic Chimps published it."

function Test-InsecureSkip { return $env:BELLA_INSECURE_SKIP_SIGNATURE -eq "1" }

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

# Extract the expected SHA256 hash for a given filename from checksums.txt (exact name match)
function Get-ExpectedHash {
    param([string]$ChecksumFile, [string]$AssetName)
    $line = Get-Content $ChecksumFile | Where-Object { $_ -match "^\S+\s+\*?$([regex]::Escape($AssetName))$" } | Select-Object -First 1
    if (-not $line) { return $null }
    return ($line -split '\s+')[0].Trim()
}

# gpg from PATH, else the copies Git for Windows and Gpg4win install without putting them on PATH.
function Find-Gpg {
    $cmd = Get-Command gpg -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $candidates = @()
    foreach ($root in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if ($root) {
            $candidates += (Join-Path $root "Git\usr\bin\gpg.exe")
            $candidates += (Join-Path $root "GnuPG\bin\gpg.exe")
        }
    }
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    return $null
}

# Runs gpg without letting its stderr become a terminating error (Windows PowerShell 5.1 does that for
# native commands under ErrorActionPreference=Stop). Returns stdout lines; the exit code is in
# $script:GpgExitCode.
function Invoke-Gpg {
    param([string]$Gpg, [string[]]$Arguments)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $out = & $Gpg @Arguments 2>$null
        $script:GpgExitCode = $LASTEXITCODE
        return $out
    } finally {
        $ErrorActionPreference = $previous
    }
}

# Verify the detached GPG signature over checksums.txt — FAILS CLOSED (#828).
# Trust model: the only key gpg can see is the embedded one, imported into a throwaway homedir (the
# user's own keyring is never read or written), and a signature is accepted only when gpg's status
# output reports VALIDSIG with the pinned PRIMARY fingerprint (its last field).
function Confirm-ReleaseSignature {
    param([string]$ChecksumFile, [string]$SigFile, [string]$WorkDir)

    if (Test-InsecureSkip) {
        Write-Warn "========================================================================"
        Write-Warn " WARNING: BELLA_INSECURE_SKIP_SIGNATURE=1 - the GPG signature is NOT checked."
        Write-Warn " Only the SHA-256 checksum is verified: that proves the download is intact,"
        Write-Warn " NOT that Cosmic Chimps published it. Do not use this outside air-gapped setups."
        Write-Warn "========================================================================"
        return
    }

    if (-not (Test-Path $SigFile) -or (Get-Item $SigFile).Length -eq 0) {
        Write-Err ("No GPG signature (checksums.txt.asc) could be downloaded for v$Version.`n" +
                   "  Releases before v$FirstSignedRelease were never signed and cannot be verified; for a later`n" +
                   "  release the download failed. Refusing to install an unverified release.`n" +
                   "  Install v$FirstSignedRelease or newer, or retry.`n$RefusalHint")
    }

    $gpg = Find-Gpg
    if (-not $gpg) {
        Write-Err ("gpg is required to verify the release signature and was not found.`n" +
                   "  Install Git for Windows or Gpg4win (winget install GnuPG.Gpg4win) and retry.`n$RefusalHint")
    }

    Write-Info "Verifying GPG signature..."

    $gnupgHome = Join-Path $WorkDir "gnupg"
    New-Item -ItemType Directory -Force -Path $gnupgHome | Out-Null
    $keyFile = Join-Path $WorkDir "bella-signing-key.asc"
    [System.IO.File]::WriteAllText($keyFile, $SigningKey, [System.Text.Encoding]::ASCII)

    try {
        Invoke-Gpg -Gpg $gpg -Arguments @("--homedir", $gnupgHome, "--batch", "--quiet", "--import", $keyFile) | Out-Null
        if ($script:GpgExitCode -ne 0) {
            Write-Err ("Could not import the embedded Cosmic Chimps signing key ($SigningFingerprint).`n" +
                       "  Your gpg ($gpg) may be too old or broken. Refusing to install an unverified release.`n$RefusalHint")
        }

        $status = Invoke-Gpg -Gpg $gpg -Arguments @("--homedir", $gnupgHome, "--batch", "--status-fd", "1", "--verify", $SigFile, $ChecksumFile)
        $verified = ($script:GpgExitCode -eq 0) -and [bool]($status | Where-Object {
            $_ -match '^\[GNUPG:\] VALIDSIG ' -and (($_.Trim() -split '\s+')[-1] -eq $SigningFingerprint)
        })
        if (-not $verified) {
            Write-Err ("GPG signature verification FAILED for v$Version!`n" +
                       "  checksums.txt is not signed by the Cosmic Chimps release key ($SigningFingerprint).`n" +
                       "  This may indicate tampering. Aborting.`n$RefusalHint")
        }
    } finally {
        $gpgconf = Join-Path (Split-Path $gpg) "gpgconf.exe"
        if (Test-Path $gpgconf) { Invoke-Gpg -Gpg $gpgconf -Arguments @("--homedir", $gnupgHome, "--kill", "all") | Out-Null }
    }

    Write-Success "GPG signature verified ✓ (key $SigningFingerprint)"
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

if ($env:BELLA_SKIP_GPG -eq "1") {
    Write-Warn "BELLA_SKIP_GPG is no longer honoured - the signature is always verified."
    Write-Warn "The only opt-out is `$env:BELLA_INSECURE_SKIP_SIGNATURE=`"1`" (air-gapped installs only)."
}

$arch = Get-Arch
Write-Info "Detected architecture: $arch"

# Resolve version: BELLA_VERSION / -Version, else the release this script was published with, else latest.
if (-not $Version) { $Version = $ReleaseVersion }
if (-not $Version) {
    Write-Info "Fetching latest release..."
    $Version = Get-LatestVersion
}
$Version = $Version -replace '^v', ''
if ($Version -notmatch '^[0-9A-Za-z][0-9A-Za-z.+-]*$') {
    Write-Err "Could not determine a valid version to install (got '$Version')."
}
Write-Info "Installing version: $Version"

# Asset name matches filenames published by the release workflow
$assetName = "cli-win-$arch.exe"
$baseUrl   = "https://github.com/$Repo/releases/download/v$Version"
$downloadUrl  = "$baseUrl/$assetName"
$checksumsUrl = "$baseUrl/checksums.txt"
$sigUrl       = "$baseUrl/checksums.txt.asc"

# Temp paths
$workDir      = Join-Path ([System.IO.Path]::GetTempPath()) ("bella-install-" + [System.Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $workDir | Out-Null
$tmpFile      = Join-Path $workDir $assetName
$tmpChecksums = Join-Path $workDir "checksums.txt"
$tmpSig       = Join-Path $workDir "checksums.txt.asc"

try {
    # Download checksums.txt first (required)
    Write-Info "Downloading checksums.txt ..."
    Invoke-WebRequest -Uri $checksumsUrl -OutFile $tmpChecksums -UseBasicParsing

    # Download the GPG signature. Its absence is judged by Confirm-ReleaseSignature (a refusal).
    if (-not (Test-InsecureSkip)) {
        try {
            Invoke-WebRequest -Uri $sigUrl -OutFile $tmpSig -UseBasicParsing -ErrorAction Stop
        } catch {
            if (Test-Path $tmpSig) { Remove-Item $tmpSig -Force -ErrorAction SilentlyContinue }
        }
    }

    # Download binary
    Write-Info "Downloading $assetName ..."
    Invoke-WebRequest -Uri $downloadUrl -OutFile $tmpFile -UseBasicParsing

    # Authenticate the manifest first, then check the binary against it — both hard fail.
    Confirm-ReleaseSignature -ChecksumFile $tmpChecksums -SigFile $tmpSig -WorkDir $workDir

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
    if (Test-Path $workDir) { Remove-Item $workDir -Recurse -Force -ErrorAction SilentlyContinue }
}
