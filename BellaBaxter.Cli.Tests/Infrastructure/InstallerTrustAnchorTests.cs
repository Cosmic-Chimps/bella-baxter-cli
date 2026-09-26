using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BellaBaxter.Cli.Tests.Infrastructure;

/// <summary>
/// Issue #828 — the installers' trust anchor exists in several files and they must agree.
/// </summary>
/// <remarks>
/// <para>The installers fail closed: a release is installed only when its <c>checksums.txt</c> carries a
/// signature made by ONE pinned key. That key is written down in several places — the committed
/// <c>scripts/bella-signing-key.asc</c>, embedded in <c>install-bella.sh</c> and <c>install-bella.ps1</c>,
/// and (by fingerprint) in the setup Action's <c>fetch-installer.sh</c>. If any copy drifts, one
/// installer refuses every genuine release while the others accept it, and nothing at build time
/// notices: the release workflow checks the embedded key against the signing secret only when a
/// release is cut.</para>
///
/// <para>The fingerprint is COMPUTED from the embedded key (a v4 fingerprint is SHA-1 over the primary
/// public-key packet), so "the key and the fingerprint agree" is established rather than assumed — no
/// gpg is needed on the test machine.</para>
/// </remarks>
public class InstallerTrustAnchorTests
{
    private const string PinnedFingerprint = "65BB8D3CEEE3DD9E4FFD22B4119F114CA309C2FA";

    [Fact]
    public void Both_installers_embed_exactly_the_committed_public_key()
    {
        var committed = KeyBlock(Script("bella-signing-key.asc"));

        Assert.Equal(committed, KeyBlock(Script("install-bella.sh")));
        Assert.Equal(committed, KeyBlock(Script("install-bella.ps1")));
    }

    [Fact]
    public void The_embedded_key_IS_the_pinned_fingerprint()
    {
        Assert.Equal(PinnedFingerprint, V4Fingerprint(KeyBlock(Script("bella-signing-key.asc"))));
    }

    [Fact]
    public void Both_installers_pin_the_same_fingerprint()
    {
        Assert.Equal(PinnedFingerprint,
            Single(Script("install-bella.sh"), "^SIGNING_FINGERPRINT=\"([0-9A-F]{40})\"$"));
        Assert.Equal(PinnedFingerprint,
            Single(Script("install-bella.ps1"), "^\\$SigningFingerprint = \"([0-9A-F]{40})\"$"));
    }

    [Fact]
    public void The_setup_Action_pins_the_same_fingerprint()
    {
        // The Action lives in the monorepo only; the public CLI repository has no copy of it.
        var action = MonorepoFile("apps/actions/setup/fetch-installer.sh");
        Assert.SkipWhen(action is null, "not a monorepo checkout — the setup Action is not present");

        Assert.Equal(PinnedFingerprint, Single(action!, "^SIGNING_FINGERPRINT=\"([0-9A-F]{40})\"$"));
    }

    [Fact]
    public void Each_installer_carries_the_release_version_placeholder_exactly_once()
    {
        // publish.yml stamps the version of the release a copy is an asset of; it refuses the release
        // unless it matches exactly once, so catch a second occurrence here rather than at release time.
        foreach (var name in new[] { "install-bella.sh", "install-bella.ps1" })
        {
            var count = Regex.Matches(Script(name), "@BELLA_RELEASE_VERSION@").Count;
            Assert.True(count == 1, $"{name}: expected the placeholder exactly once, found {count}");
        }
    }

    [Fact]
    public void No_installer_or_instruction_fetches_the_installer_from_a_branch()
    {
        // `main` of the public CLI repository is force-rewritten by the monorepo mirror; nothing may
        // tell anyone to execute a script from it. A tag or a release asset is the only acceptable ref.
        var root = MonorepoRoot() ?? CliRoot();
        var offenders = new List<string>();
        var branchFetch = new Regex(@"bella-baxter-cli/(main|master|development)/scripts/", RegexOptions.IgnoreCase);

        foreach (var file in Walk(new DirectoryInfo(root)))
        {
            if (branchFetch.IsMatch(File.ReadAllText(file.FullName)))
                offenders.Add(Path.GetRelativePath(root, file.FullName));
        }

        Assert.True(offenders.Count == 0,
            "These still fetch the installer from a branch of bella-baxter-cli (#828):\n  "
            + string.Join("\n  ", offenders)
            + "\nUse https://github.com/Cosmic-Chimps/bella-baxter-cli/releases/latest/download/install-bella.sh "
            + "(or …/releases/download/vX.Y.Z/…) instead.");
    }

    [Fact]
    public void The_scan_actually_reads_files()
    {
        var root = MonorepoRoot() ?? CliRoot();
        var count = Walk(new DirectoryInfo(root)).Count();
        Assert.True(count > 20, $"only {count} files scanned under '{root}' — the anchor has drifted");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string Script(string name) =>
        File.ReadAllText(Path.Combine(CliRoot(), "scripts", name));

    /// <summary>The armored block, line endings normalized, headers included.</summary>
    private static string KeyBlock(string text)
    {
        var normalized = text.Replace("\r\n", "\n");
        var match = Regex.Match(normalized,
            "-----BEGIN PGP PUBLIC KEY BLOCK-----\n.*?\n-----END PGP PUBLIC KEY BLOCK-----",
            RegexOptions.Singleline);
        Assert.True(match.Success, "no armored public key block found");
        return match.Value;
    }

    private static string Single(string text, string pattern)
    {
        var matches = Regex.Matches(text.Replace("\r\n", "\n"), pattern, RegexOptions.Multiline);
        Assert.True(matches.Count == 1, $"expected exactly one match for {pattern}, found {matches.Count}");
        return matches[0].Groups[1].Value;
    }

    /// <summary>RFC 4880 §12.2: SHA-1 over 0x99, a two-octet length, and the primary key packet body.</summary>
    private static string V4Fingerprint(string armored)
    {
        var lines = armored.Split('\n');
        var body = new StringBuilder();
        var inBody = false;
        foreach (var line in lines.Skip(1))
        {
            if (line.StartsWith("-----END", StringComparison.Ordinal) || line.StartsWith('=')) break;
            if (!inBody) { inBody = line.Length == 0; continue; }   // armor headers end at a blank line
            body.Append(line.Trim());
        }

        var bytes = Convert.FromBase64String(body.ToString());
        var ctb = bytes[0];
        Assert.True((ctb & 0x80) != 0, "not an OpenPGP packet");

        int tag, length, offset;
        if ((ctb & 0x40) != 0)
        {
            // New-format packet header.
            tag = ctb & 0x3F;
            var first = bytes[1];
            if (first < 192) { length = first; offset = 2; }
            else if (first < 224) { length = ((first - 192) << 8) + bytes[2] + 192; offset = 3; }
            else if (first == 255) { length = (bytes[2] << 24) | (bytes[3] << 16) | (bytes[4] << 8) | bytes[5]; offset = 6; }
            else throw new InvalidOperationException("partial-length key packet");
        }
        else
        {
            // Old-format packet header.
            tag = (ctb >> 2) & 0x0F;
            switch (ctb & 0x03)
            {
                case 0: length = bytes[1]; offset = 2; break;
                case 1: length = (bytes[1] << 8) | bytes[2]; offset = 3; break;
                case 2: length = (bytes[1] << 24) | (bytes[2] << 16) | (bytes[3] << 8) | bytes[4]; offset = 5; break;
                default: throw new InvalidOperationException("indeterminate-length key packet");
            }
        }

        Assert.Equal(6, tag);          // Public-Key packet
        Assert.Equal(4, bytes[offset]); // version 4

        var material = new byte[3 + length];
        material[0] = 0x99;
        material[1] = (byte)(length >> 8);
        material[2] = (byte)length;
        Array.Copy(bytes, offset, material, 3, length);

        return Convert.ToHexString(SHA1.HashData(material));
    }

    private static readonly string[] SkippedDirectories =
        ["node_modules", "bin", "obj", ".git", ".nuxt", ".output", "dist", "specs", "issues", ".claude", "TestResults"];

    private static readonly string[] ScannedExtensions =
        [".md", ".sh", ".ps1", ".yml", ".yaml", ".tpl", ".tf"];

    private static IEnumerable<FileInfo> Walk(DirectoryInfo dir)
    {
        foreach (var file in dir.EnumerateFiles())
        {
            if (ScannedExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase)
                || file.Name.StartsWith("Dockerfile", StringComparison.Ordinal))
                yield return file;
        }

        foreach (var sub in dir.EnumerateDirectories())
        {
            if (SkippedDirectories.Contains(sub.Name, StringComparer.Ordinal)) continue;
            foreach (var file in Walk(sub)) yield return file;
        }
    }

    /// <summary>The CLI's own root: <c>apps/cli-dotnet</c> in the monorepo, the repository root in the public mirror.</summary>
    private static string CliRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "scripts", "install-bella.sh")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string? MonorepoRoot()
    {
        var dir = new DirectoryInfo(CliRoot());
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "apps", "cli-dotnet")))
            dir = dir.Parent;
        return dir?.FullName;
    }

    private static string? MonorepoFile(string relative)
    {
        var root = MonorepoRoot();
        if (root is null) return null;
        var path = Path.Combine(root, relative);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }
}
