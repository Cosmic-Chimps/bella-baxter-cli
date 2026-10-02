using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace BellaCli.Commands.Upgrade;

/// <summary>What checking a release's <c>checksums.txt.asc</c> concluded. Everything but <see cref="Verified"/> refuses.</summary>
public enum SignatureVerdict
{
    /// <summary>gpg reported a VALID signature made by the pinned primary key.</summary>
    Verified,

    /// <summary>The release publishes no signature, or it could not be downloaded.</summary>
    NoSignature,

    /// <summary>No gpg executable could be found, so nothing could be checked.</summary>
    GpgMissing,

    /// <summary>gpg would not import the embedded key (a gpg too old or broken to trust its verdict).</summary>
    KeyImportFailed,

    /// <summary>The signature is bad, made by another key, or over different bytes.</summary>
    Invalid,
}

/// <summary>
/// Issue #1051 — provenance for <c>bella upgrade</c>: <c>checksums.txt</c> must carry a signature made by the
/// Cosmic Chimps release key, the same check <c>install-bella.sh</c>, <c>install-bella.ps1</c> and the setup
/// Action's <c>fetch-installer.sh</c> have made since #828.
/// </summary>
/// <remarks>
/// <para><b>The same trust model as the installers, on purpose.</b> The only key gpg can see is the embedded one,
/// imported into a throwaway home directory (the user's own keyring is never read or written), and a signature is
/// accepted only when gpg exits successfully AND its machine-readable status reports <c>VALIDSIG</c> whose LAST
/// field — the fingerprint of the PRIMARY key that made it — is <see cref="PinnedFingerprint"/>. A key in the user's
/// keyring, a keyserver, or a key shipped next to the release can therefore never vouch for a release.</para>
///
/// <para><b>One copy of the key.</b> The key is <c>scripts/bella-signing-key.asc</c> itself, embedded into the binary
/// at build time — not a fifth hand-copied block. <c>InstallerTrustAnchorTests</c> holds the fingerprint constant
/// here equal to the installers' and to the one computed from that file.</para>
///
/// <para><b>Fails closed.</b> A missing signature, a missing gpg, a key that will not import, or a signature gpg
/// does not accept all refuse the upgrade. The single opt-out is the installers' own
/// <c>BELLA_INSECURE_SKIP_SIGNATURE=1</c>, which skips this check only, loudly; the SHA-256 check still runs.</para>
/// </remarks>
public static class ReleaseSignature
{
    /// <summary>The release-signing key's v4 fingerprint — the trust anchor. Equal to the installers' (tested).</summary>
    public const string PinnedFingerprint = "65BB8D3CEEE3DD9E4FFD22B4119F114CA309C2FA";

    /// <summary>The detached, armored signature over <see cref="ReleaseSource.ChecksumsAssetName"/>.</summary>
    public const string SignatureAssetName = "checksums.txt.asc";

    /// <summary>The air-gapped escape hatch, the same variable the installers honour.</summary>
    public const string InsecureSkipVariable = "BELLA_INSECURE_SKIP_SIGNATURE";

    public static bool InsecureSkipRequested =>
        string.Equals(Environment.GetEnvironmentVariable(InsecureSkipVariable), "1", StringComparison.Ordinal);

    /// <summary>The committed <c>scripts/bella-signing-key.asc</c>, as embedded at build time.</summary>
    public static string EmbeddedPublicKey { get; } = ReadEmbeddedKey();

    private static string ReadEmbeddedKey()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("bella-signing-key.asc")
            ?? throw new InvalidOperationException("The release-signing key is not embedded in this build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Whether gpg's <c>--status-fd</c> output accepts the signature: a <c>VALIDSIG</c> line whose last field is
    /// <paramref name="fingerprint"/>. Pure, so the rule is testable without gpg.
    /// </summary>
    public static bool StatusAccepts(string statusOutput, string fingerprint)
    {
        foreach (var raw in (statusOutput ?? "").Split('\n'))
        {
            var fields = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 3 && fields[0] == "[GNUPG:]" && fields[1] == "VALIDSIG"
                && string.Equals(fields[^1], fingerprint, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Verifies <paramref name="signaturePath"/> over <paramref name="dataPath"/> with the pinned key.</summary>
    public static Task<SignatureVerdict> VerifyAsync(string dataPath, string signaturePath, CancellationToken ct) =>
        VerifyAsync(dataPath, signaturePath, EmbeddedPublicKey, PinnedFingerprint, FindGpg(), ct);

    /// <summary>The verification itself, with the key, the fingerprint and the gpg executable supplied (tests).</summary>
    public static async Task<SignatureVerdict> VerifyAsync(
        string dataPath, string signaturePath, string publicKey, string fingerprint, string? gpg, CancellationToken ct)
    {
        if (!File.Exists(signaturePath) || new FileInfo(signaturePath).Length == 0)
            return SignatureVerdict.NoSignature;
        if (gpg is null || !File.Exists(gpg))
            return SignatureVerdict.GpgMissing;

        var home = Directory.CreateTempSubdirectory("bella-gpg-");
        try
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                File.SetUnixFileMode(home.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var import = await RunAsync(gpg, ["--homedir", home.FullName, "--batch", "--quiet", "--import"], publicKey, ct);
            if (import.ExitCode != 0)
                return SignatureVerdict.KeyImportFailed;

            var verify = await RunAsync(gpg,
                ["--homedir", home.FullName, "--batch", "--status-fd", "1", "--verify", signaturePath, dataPath], null, ct);

            return verify.ExitCode == 0 && StatusAccepts(verify.Stdout, fingerprint)
                ? SignatureVerdict.Verified
                : SignatureVerdict.Invalid;
        }
        finally
        {
            await StopAgentsAsync(gpg, home.FullName);
            try { home.Delete(recursive: true); } catch { /* best effort: a leftover temp dir must not mask the verdict */ }
        }
    }

    /// <summary>gpg from PATH, else the copies Git for Windows and Gpg4win install without putting them on PATH.</summary>
    public static string? FindGpg()
    {
        var windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var name = windows ? "gpg.exe" : "gpg";

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length == 0) continue;
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
        }

        if (!windows) return null;
        foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)") })
        {
            if (string.IsNullOrEmpty(root)) continue;
            foreach (var candidate in new[] { Path.Combine(root, "Git", "usr", "bin", "gpg.exe"), Path.Combine(root, "GnuPG", "bin", "gpg.exe") })
                if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static async Task<(int ExitCode, string Stdout)> RunAsync(string exe, string[] args, string? stdin, CancellationToken ct)
    {
        var info = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) info.ArgumentList.Add(a);

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {exe}.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct); // drained so gpg never blocks on a full pipe
        if (stdin is not null) await process.StandardInput.WriteAsync(stdin.AsMemory(), ct);
        process.StandardInput.Close();
        await process.WaitForExitAsync(ct);
        await stderr;
        return (process.ExitCode, await stdout);
    }

    /// <summary>gpg may start an agent/keyboxd in the throwaway home; stop it before the directory is removed.</summary>
    private static async Task StopAgentsAsync(string gpg, string home)
    {
        var gpgconf = Path.Combine(Path.GetDirectoryName(gpg) ?? "",
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "gpgconf.exe" : "gpgconf");
        if (!File.Exists(gpgconf)) return;
        try { await RunAsync(gpgconf, ["--homedir", home, "--kill", "all"], null, CancellationToken.None); }
        catch { /* best effort */ }
    }
}
