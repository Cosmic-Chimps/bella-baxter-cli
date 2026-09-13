using System.Security.Cryptography;

namespace BellaCli.Commands.Upgrade;

/// <summary>What checking a downloaded asset against the release manifest concluded.</summary>
public enum ChecksumVerdict
{
    /// <summary>The file's SHA-256 is the one the release published for it.</summary>
    Verified,

    /// <summary>The manifest lists this asset and the file does not match it.</summary>
    Mismatch,

    /// <summary>The manifest does not list this asset at all — which is a refusal, not a pass.</summary>
    NotListed,
}

/// <param name="Expected">The published digest, or <c>null</c> when the asset was not listed.</param>
public readonly record struct ChecksumVerification(ChecksumVerdict Verdict, string? Expected, string Actual)
{
    public bool IsVerified => Verdict == ChecksumVerdict.Verified;
}

/// <summary>
/// The <c>checksums.txt</c> a release publishes, and the verdict on one downloaded asset.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> <c>bella upgrade</c> downloaded a binary over the network and moved it
/// straight over the running executable, with nothing checked in between. The project already generates
/// the manifest — <c>publish.yml</c> runs <c>sha256sum cli-* &gt; checksums.txt</c> on every release —
/// and the release notes tell operators to run <c>sha256sum --check</c> themselves. The one code path
/// that actually replaces the binary was the only consumer skipping it.</para>
///
/// <para><b>An absent or unparseable entry is a REFUSAL, never a skip</b> (<see cref="ChecksumVerdict.NotListed"/>).
/// Treating a missing manifest as "nothing to check" would mean anyone able to remove one asset could
/// switch verification off for everyone, which is worse than not having it — it would read as protection
/// while providing none. Every release back to <c>v0.1.1-preview.90</c> carries the manifest and
/// <c>publish.yml</c> generates it unconditionally, so failing closed costs no working upgrade.</para>
///
/// <para><b>What this does and does not prove.</b> The manifest travels the same channel as the binary,
/// so this establishes INTEGRITY — the bytes are the bytes that release published, not a truncated or
/// corrupted download — and not PROVENANCE. Provenance is the sibling <c>checksums.txt.asc</c>, and
/// verifying it needs a signing key shipped with and rotatable by the CLI; <c>publish.yml</c> also only
/// signs when a key is configured, so a hard requirement would break unsigned releases. That is a
/// separate piece of work, deliberately not implied by this one.</para>
/// </remarks>
public sealed class ReleaseChecksums
{
    private readonly Dictionary<string, string> _byAsset;

    private ReleaseChecksums(Dictionary<string, string> byAsset) => _byAsset = byAsset;

    /// <summary>How many usable entries were understood. Zero is a legitimate answer, and refuses everything.</summary>
    public int Count => _byAsset.Count;

    /// <summary>
    /// Parses <c>sha256sum</c> output: a hex digest, whitespace, then the file name. GNU coreutils marks
    /// binary mode with a <c>*</c> before the name, which is accepted and stripped.
    /// </summary>
    /// <remarks>
    /// A line that is not understood is DROPPED rather than guessed at. The asset it referred to then
    /// reads as <see cref="ChecksumVerdict.NotListed"/> and the upgrade refuses — the safe direction. A
    /// lenient parser here would be inventing an expectation out of a line it could not read.
    /// </remarks>
    public static ReleaseChecksums Parse(string manifest)
    {
        var byAsset = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var raw in (manifest ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var split = line.IndexOfAny([' ', '\t']);
            if (split <= 0) continue;

            var digest = line[..split];
            if (!IsSha256Hex(digest)) continue;

            var name = line[split..].TrimStart(' ', '\t');
            if (name.StartsWith('*')) name = name[1..];
            name = name.Trim();
            if (name.Length == 0) continue;

            // First entry wins: a duplicated name is ambiguous, and picking the later one would let an
            // appended line override a published digest.
            byAsset.TryAdd(name, digest.ToLowerInvariant());
        }

        return new ReleaseChecksums(byAsset);
    }

    /// <summary>The verdict for one asset, given the digest actually computed over the downloaded file.</summary>
    public ChecksumVerification Verify(string assetName, string actualSha256Hex)
    {
        var actual = (actualSha256Hex ?? "").Trim().ToLowerInvariant();

        if (!_byAsset.TryGetValue(assetName, out var expected))
            return new ChecksumVerification(ChecksumVerdict.NotListed, null, actual);

        return new ChecksumVerification(
            string.Equals(expected, actual, StringComparison.Ordinal) ? ChecksumVerdict.Verified : ChecksumVerdict.Mismatch,
            expected,
            actual);
    }

    /// <summary>SHA-256 of a file, lower-case hex — the shape <c>sha256sum</c> prints.</summary>
    public static async Task<string> ComputeFileSha256Async(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool IsSha256Hex(string value)
    {
        if (value.Length != 64) return false;

        foreach (var c in value)
        {
            var isHex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!isHex) return false;
        }

        return true;
    }
}
