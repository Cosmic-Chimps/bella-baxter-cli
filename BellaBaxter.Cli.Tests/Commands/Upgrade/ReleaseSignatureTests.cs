using System.Diagnostics;
using BellaCli.Commands.Upgrade;

namespace BellaBaxter.Cli.Tests.Commands.Upgrade;

/// <summary>
/// Issue #1051 — <c>bella upgrade</c> replaces the running binary only after <c>checksums.txt</c> carries a
/// signature made by the pinned release key, the check the installers have made since #828.
/// </summary>
/// <remarks>
/// The behaviour tests use REAL gpg and real throwaway keys rather than a fake: the defect class here is "a check
/// that reads as protection while providing none", and only gpg itself can say what gpg accepts. They skip on a
/// machine without gpg; the status-parsing rule and the source-order guard need none.
/// </remarks>
public class ReleaseSignatureTests
{
    // ── the rule, without gpg ────────────────────────────────────────────────

    private const string Fpr = "65BB8D3CEEE3DD9E4FFD22B4119F114CA309C2FA";

    [Fact]
    public void A_VALIDSIG_whose_last_field_is_the_pinned_primary_key_is_accepted()
    {
        // VALIDSIG <signing-key fpr> <date> <ts> <exp> <ver> <res> <pk-algo> <hash-algo> <class> <PRIMARY fpr>
        var status = "[GNUPG:] NEWSIG\n[GNUPG:] GOODSIG 119F114CA309C2FA Cosmic Chimps\n" +
                     $"[GNUPG:] VALIDSIG AAAABBBBCCCCDDDDEEEEFFFF0000111122223333 2026-10-02 1759400000 0 4 0 22 10 00 {Fpr}\n";
        Assert.True(ReleaseSignature.StatusAccepts(status, Fpr));
    }

    [Fact]
    public void A_signature_by_any_other_key_or_no_VALIDSIG_at_all_is_refused()
    {
        Assert.False(ReleaseSignature.StatusAccepts(
            "[GNUPG:] VALIDSIG 1111 2026-10-02 1 0 4 0 22 10 00 1111222233334444555566667777888899990000\n", Fpr));
        Assert.False(ReleaseSignature.StatusAccepts($"[GNUPG:] GOODSIG {Fpr} someone\n", Fpr));
        Assert.False(ReleaseSignature.StatusAccepts($"[GNUPG:] BADSIG {Fpr} someone\n", Fpr));
        // The fingerprint appearing anywhere but VALIDSIG's last field is not acceptance.
        Assert.False(ReleaseSignature.StatusAccepts($"[GNUPG:] VALIDSIG {Fpr} 2026-10-02 1 0 4 0 22 10 00 OTHER\n", Fpr));
        Assert.False(ReleaseSignature.StatusAccepts("", Fpr));
    }

    [Fact]
    public void The_embedded_key_is_the_committed_scripts_file()
    {
        // One copy: the binary carries scripts/bella-signing-key.asc itself, not a hand-copied block.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "scripts", "bella-signing-key.asc")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        Assert.Equal(
            File.ReadAllText(Path.Combine(dir!.FullName, "scripts", "bella-signing-key.asc")).ReplaceLineEndings("\n"),
            ReleaseSignature.EmbeddedPublicKey.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void The_binary_is_replaced_only_after_the_signature_is_verified()
    {
        // The defect, as a source property: UpgradeCommand moved the download over the running binary after a
        // SHA-256 check against a manifest from the same release, and no signature was checked anywhere.
        var source = File.ReadAllText(Path.Combine(CliProjectDir(), "Commands", "Upgrade", "UpgradeCommand.cs"));
        Assert.True(source.Contains("ReleaseSignature.VerifyAsync(", StringComparison.Ordinal),
            "UpgradeCommand never verifies checksums.txt.asc (#1051)");

        // The verification helper is CALLED before the replace, and a non-Verified verdict returns before it.
        var call = source.IndexOf("await VerifyManifestSignatureAsync(", StringComparison.Ordinal);
        var refusal = source.IndexOf("if (verdict != SignatureVerdict.Verified)", StringComparison.Ordinal);
        var download = source.IndexOf("AnsiConsole.Progress()", StringComparison.Ordinal);
        // #1238 moved the renames into RunningBinary.Replace; this is the one call that performs them.
        var replace = source.IndexOf("RunningBinary.Replace(currentExe", StringComparison.Ordinal);
        Assert.True(call >= 0 && refusal > call, "the signature verdict must be checked where it is obtained");
        Assert.True(download > refusal, "the signature must be verified BEFORE the binary is downloaded");
        Assert.True(replace > download, "…and therefore before the running binary is replaced");
    }

    // ── the behaviour, with real gpg ─────────────────────────────────────────

    [Fact]
    public async Task A_genuine_signature_by_the_pinned_key_is_verified()
    {
        using var keys = GpgFixture.CreateOrSkip();
        var (data, sig) = keys.SignedManifest("abc123  cli-linux-x64\n");

        Assert.Equal(SignatureVerdict.Verified,
            await ReleaseSignature.VerifyAsync(data, sig, keys.PublicKey, keys.Fingerprint, keys.Gpg, CancellationToken.None));
    }

    [Fact]
    public async Task A_manifest_changed_after_signing_is_refused()
    {
        using var keys = GpgFixture.CreateOrSkip();
        var (data, sig) = keys.SignedManifest("abc123  cli-linux-x64\n");
        File.WriteAllText(data, "def456  cli-linux-x64\n");

        Assert.Equal(SignatureVerdict.Invalid,
            await ReleaseSignature.VerifyAsync(data, sig, keys.PublicKey, keys.Fingerprint, keys.Gpg, CancellationToken.None));
    }

    [Fact]
    public async Task A_valid_signature_by_ANOTHER_key_is_refused()
    {
        // The attacker's key is imported too (they could ship it beside the release): only the pinned
        // fingerprint can vouch, so a perfectly valid signature by another key still fails.
        using var pinned = GpgFixture.CreateOrSkip();
        using var other = GpgFixture.CreateOrSkip();
        var (data, sig) = other.SignedManifest("abc123  cli-linux-x64\n");

        Assert.Equal(SignatureVerdict.Invalid,
            await ReleaseSignature.VerifyAsync(data, sig, other.PublicKey, pinned.Fingerprint, pinned.Gpg, CancellationToken.None));
        Assert.Equal(SignatureVerdict.Invalid,
            await ReleaseSignature.VerifyAsync(data, sig, pinned.PublicKey, pinned.Fingerprint, pinned.Gpg, CancellationToken.None));
    }

    [Fact]
    public async Task No_signature_no_gpg_and_an_unimportable_key_each_refuse()
    {
        using var keys = GpgFixture.CreateOrSkip();
        var (data, sig) = keys.SignedManifest("abc123  cli-linux-x64\n");

        Assert.Equal(SignatureVerdict.NoSignature, await ReleaseSignature.VerifyAsync(
            data, Path.Combine(keys.Dir, "absent.asc"), keys.PublicKey, keys.Fingerprint, keys.Gpg, CancellationToken.None));
        Assert.Equal(SignatureVerdict.GpgMissing, await ReleaseSignature.VerifyAsync(
            data, sig, keys.PublicKey, keys.Fingerprint, Path.Combine(keys.Dir, "no-gpg-here"), CancellationToken.None));
        Assert.Equal(SignatureVerdict.KeyImportFailed, await ReleaseSignature.VerifyAsync(
            data, sig, "not a key", keys.Fingerprint, keys.Gpg, CancellationToken.None));
    }

    private static string CliProjectDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BellaBaxter.Cli", "BellaBaxter.Cli.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "BellaBaxter.Cli");
    }

    /// <summary>A throwaway gpg home with one fresh signing key.</summary>
    private sealed class GpgFixture : IDisposable
    {
        public required string Gpg { get; init; }
        public required string Dir { get; init; }
        public required string PublicKey { get; init; }
        public required string Fingerprint { get; init; }
        private string Home => Path.Combine(Dir, "home");

        public static GpgFixture CreateOrSkip()
        {
            var gpg = ReleaseSignature.FindGpg();
            Assert.SkipWhen(gpg is null, "gpg is not installed on this machine");

            var dir = Directory.CreateTempSubdirectory("bella-sigtest-").FullName;
            var home = Path.Combine(dir, "home");
            Directory.CreateDirectory(home);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            Run(gpg!, home, "--batch", "--pinentry-mode", "loopback", "--passphrase", "",
                "--quick-gen-key", $"Test {Guid.NewGuid():N} <t@example.test>", "ed25519", "sign", "never");
            var colons = Run(gpg!, home, "--batch", "--with-colons", "--fingerprint");
            var fpr = colons.Split('\n').First(l => l.StartsWith("fpr:", StringComparison.Ordinal)).Split(':')[9];
            var pub = Run(gpg!, home, "--batch", "--armor", "--export", fpr);

            return new GpgFixture { Gpg = gpg!, Dir = dir, PublicKey = pub, Fingerprint = fpr };
        }

        public (string Data, string Signature) SignedManifest(string content)
        {
            var data = Path.Combine(Dir, $"checksums-{Guid.NewGuid():N}.txt");
            File.WriteAllText(data, content);
            var sig = data + ".asc";
            Run(Gpg, Home, "--batch", "--pinentry-mode", "loopback", "--passphrase", "",
                "--armor", "--detach-sign", "--output", sig, data);
            return (data, sig);
        }

        private static string Run(string gpg, string home, params string[] args)
        {
            var info = new ProcessStartInfo(gpg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            info.ArgumentList.Add("--homedir");
            info.ArgumentList.Add(home);
            foreach (var a in args) info.ArgumentList.Add(a);
            using var p = Process.Start(info)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, $"gpg {string.Join(' ', args)} failed: {stderr}");
            return stdout.Result;
        }

        public void Dispose()
        {
            try
            {
                var gpgconf = Path.Combine(Path.GetDirectoryName(Gpg) ?? "", OperatingSystem.IsWindows() ? "gpgconf.exe" : "gpgconf");
                if (File.Exists(gpgconf))
                    Process.Start(new ProcessStartInfo(gpgconf, ["--homedir", Home, "--kill", "all"]) { UseShellExecute = false })?.WaitForExit();
                Directory.Delete(Dir, recursive: true);
            }
            catch { /* best effort */ }
        }
    }
}
