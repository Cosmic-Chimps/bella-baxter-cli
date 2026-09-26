# Bella CLI — Release Signing

Every release ships, as assets of that release:

| Asset | What it is |
|---|---|
| `checksums.txt` | SHA-256 of every binary **and** of `install-bella.sh` / `install-bella.ps1` |
| `checksums.txt.asc` | GPG detached signature over `checksums.txt` |
| `bella-signing-key.asc` | The public key, exported from the signing key at release time |
| `install-bella.sh`, `install-bella.ps1` | The installers, stamped with the release's own version |

The release key is **`65BB 8D3C EEE3 DD9E 4FFD  22B4 119F 114C A309 C2FA`**
(`Cosmic Chimps <it@cosmic-chimps.com>`, RSA 4096, no expiry). Every signed release since
`v0.1.1-preview.26` was signed by it.

## Trust model (#828)

- **The fingerprint is the trust anchor.** The installers and the setup Action accept a signature only
  when gpg's machine-readable status reports `VALIDSIG` with that exact **primary** fingerprint. Key
  material is transport: the installers embed the key (so no keyserver is needed), the Action
  downloads `bella-signing-key.asc` from the release, and neither can be fooled by a different key
  because neither trusts anything but the pinned fingerprint.
- **Verification runs in a throwaway `GNUPGHOME`** holding only that key. The user's own keyring is
  never read or written, so a key someone imported there cannot vouch for a release.
- **Fail closed.** A missing `.asc`, a missing `gpg`, a key that will not import, or a signature that
  does not verify each ABORT the install. The one opt-out is `BELLA_INSECURE_SKIP_SIGNATURE=1`, for
  air-gapped mirrors that cannot carry the signature; it prints a loud warning and still enforces the
  SHA-256 check. `BELLA_SKIP_GPG` is no longer honoured.
- **Pinned refs.** The installers are served from releases (`releases/download/vX/…` or
  `releases/latest/download/…`), never from a branch: `main` of this repository is force-rewritten by
  the monorepo mirror. The setup Action runs code pinned by the ref the workflow uses for it
  (`fetch-installer.sh`), which fetches the installer from the release it installs and verifies it
  before running it.

What this does NOT cover: `bella upgrade` verifies SHA-256 against `checksums.txt` but not yet the
signature (see `ReleaseChecksums.cs`). And a `curl … | bash` of `releases/latest/download/install-bella.sh`
necessarily trusts that one script as fetched over TLS; everything it downloads after that is verified.

## How CI signs (`.github/workflows/publish.yml`, job `create_release`)

1. Stages `scripts/install-bella.{sh,ps1}` into the release, stamping `@BELLA_RELEASE_VERSION@`
   (refused unless it occurs exactly once), and refuses if the key embedded in either installer
   differs from `scripts/bella-signing-key.asc`.
2. `sha256sum cli-* install-bella.sh install-bella.ps1 > checksums.txt`.
3. **Refuses to continue** if `BELLA_BAXTER_GPG_PRIVATE_KEY` is not set — an unsigned release is one
   the installers will not install.
4. Imports the secret key into a temporary `GNUPGHOME` and refuses unless it holds the secret key for
   the fingerprint the installers pin; signs with `--local-user` that fingerprint.
5. Exports the public key as `bella-signing-key.asc`.
6. Verifies the new signature using ONLY the key embedded in the shipped `install-bella.sh`, requiring
   `VALIDSIG` with the pinned fingerprint — the exact check a user's install makes.

Required repository secrets (on `Cosmic-Chimps/bella-baxter-cli`):

| Secret | Value |
|---|---|
| `BELLA_BAXTER_GPG_PRIVATE_KEY` | ASCII-armored secret key for `65BB8D3C…A309C2FA` |
| `BELLA_BAXTER_GPG_PASSPHRASE` | Its passphrase (empty if the key has none) |

`BellaBaxter.Cli.Tests/Infrastructure/InstallerTrustAnchorTests.cs` checks at build time that both
installers embed exactly the committed key, that the key's computed v4 fingerprint is the pinned one,
that the setup Action pins the same fingerprint, and that nothing documents fetching an installer from
a branch.

## Manual verification

```bash
V=0.1.1-preview.116   # the release you downloaded
B=https://github.com/Cosmic-Chimps/bella-baxter-cli/releases/download/v$V
curl -LO $B/cli-linux-x64 -LO $B/checksums.txt -LO $B/checksums.txt.asc -LO $B/bella-signing-key.asc

gpg --import bella-signing-key.asc
gpg --fingerprint 65BB8D3CEEE3DD9E4FFD22B4119F114CA309C2FA   # compare with the fingerprint above
gpg --verify checksums.txt.asc checksums.txt
sha256sum --check --ignore-missing checksums.txt
```

Older releases do not publish `bella-signing-key.asc`; use `scripts/bella-signing-key.asc` from this
repository, or `https://keys.openpgp.org/vks/v1/by-fingerprint/65BB8D3CEEE3DD9E4FFD22B4119F114CA309C2FA`.

## Key rotation

The fingerprint is pinned in four places, and all of them must change together **before** a release
is signed with a new key, or every install of that release is refused:

1. Generate the new key; publish its fingerprint out-of-band (docs, keyserver).
2. Replace `scripts/bella-signing-key.asc` and the key block embedded in `install-bella.sh` and
   `install-bella.ps1`; update `SIGNING_FINGERPRINT` / `$SigningFingerprint` in both, and
   `SIGNING_FINGERPRINT` in the setup Action's `fetch-installer.sh` (monorepo `apps/actions/setup/`),
   and `PinnedFingerprint` in `InstallerTrustAnchorTests`.
3. Update the `BELLA_BAXTER_GPG_PRIVATE_KEY` / `_PASSPHRASE` secrets.
4. Cut a release. Installers from releases signed by the old key keep verifying those releases (each
   installer is pinned to the key of its own era); a published setup Action version only accepts the
   key it pins, so users must move to an Action version that pins the new fingerprint.
5. Revoke the old key on keyservers once nothing needs it.
