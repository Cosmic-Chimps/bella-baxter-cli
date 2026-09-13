using BellaCli.Commands.Upgrade;

namespace BellaBaxter.Cli.Tests.Commands.Upgrade;

/// <summary>
/// #637 — the upgrade downloaded a binary and moved it over the running executable unverified.
/// </summary>
/// <remarks>
/// The manifest has always existed: <c>publish.yml</c> runs <c>sha256sum cli-* &gt; checksums.txt</c> on
/// every release and the release notes tell operators to check it by hand. The one code path that
/// actually replaces the binary was the only consumer skipping it.
/// </remarks>
public class ReleaseChecksumsTests
{
    // A real manifest, copied from v0.1.1-preview.101 — two spaces, lower-case hex, no trailing marker.
    private const string RealManifest = """
        656129ea72017c1c34069fb22d4ca293d2f4ed58da0f1d06dcb1b168e4ca87bc  cli-linux-arm64
        7f1ec40a40b109d23808c3d1e6e82b3baca043ed5cfa67843ce3ae974afb6e14  cli-linux-x64
        bc7e051e3cee5ac14cc49aa731bd062d757140616987e0a774a1c4ac6262d309  cli-osx-arm64
        17d6a9c553066873c059022a79c9292c47539aaf434e09ef4a2a73a93c37092a  cli-win-x64.exe
        """;

    [Fact]
    public void The_published_digest_verifies_the_matching_download()
    {
        var result = ReleaseChecksums.Parse(RealManifest)
            .Verify("cli-osx-arm64", "bc7e051e3cee5ac14cc49aa731bd062d757140616987e0a774a1c4ac6262d309");

        Assert.Equal(ChecksumVerdict.Verified, result.Verdict);
        Assert.True(result.IsVerified);
    }

    [Fact]
    public void A_different_binary_under_the_right_name_is_a_mismatch()
    {
        // The case the whole feature exists for: the asset name is right and the bytes are not.
        var result = ReleaseChecksums.Parse(RealManifest)
            .Verify("cli-osx-arm64", new string('a', 64));

        Assert.Equal(ChecksumVerdict.Mismatch, result.Verdict);
        Assert.False(result.IsVerified);
        Assert.Equal("bc7e051e3cee5ac14cc49aa731bd062d757140616987e0a774a1c4ac6262d309", result.Expected);
    }

    [Fact]
    public void An_asset_the_manifest_does_not_list_is_refused_not_waved_through()
    {
        // NotListed must never read as "nothing to check". If it did, removing one line from the
        // manifest would switch verification off for that platform while still looking protected.
        var result = ReleaseChecksums.Parse(RealManifest).Verify("cli-linux-musl-x64", new string('b', 64));

        Assert.Equal(ChecksumVerdict.NotListed, result.Verdict);
        Assert.False(result.IsVerified);
        Assert.Null(result.Expected);
    }

    [Fact]
    public void An_empty_or_unreadable_manifest_verifies_nothing()
    {
        // Every asset must fail closed rather than the parser yielding a permissive empty set that
        // silently agrees with whatever it is handed.
        foreach (var manifest in new[] { "", "   \n\n  ", "not a checksum file at all", "<html>404</html>" })
        {
            var checksums = ReleaseChecksums.Parse(manifest);
            Assert.Equal(0, checksums.Count);
            Assert.Equal(ChecksumVerdict.NotListed, checksums.Verify("cli-linux-x64", new string('c', 64)).Verdict);
        }
    }

    [Fact]
    public void Digest_comparison_ignores_case_because_the_two_sides_disagree_about_it()
    {
        // sha256sum prints lower-case; .NET's Convert.ToHexString returns upper-case. A case-sensitive
        // comparison would refuse every genuine upgrade — a fail-closed bug, but still a bug.
        var upper = "BC7E051E3CEE5AC14CC49AA731BD062D757140616987E0A774A1C4AC6262D309";

        Assert.Equal(
            ChecksumVerdict.Verified,
            ReleaseChecksums.Parse(RealManifest).Verify("cli-osx-arm64", upper).Verdict);
    }

    [Fact]
    public void Binary_mode_entries_and_odd_whitespace_are_understood()
    {
        // GNU coreutils writes `*name` in binary mode, and a single space is equally valid.
        var manifest = "656129ea72017c1c34069fb22d4ca293d2f4ed58da0f1d06dcb1b168e4ca87bc *cli-linux-arm64\n"
                     + "\t7f1ec40a40b109d23808c3d1e6e82b3baca043ed5cfa67843ce3ae974afb6e14\tcli-linux-x64\t\n";

        var checksums = ReleaseChecksums.Parse(manifest);

        Assert.Equal(2, checksums.Count);
        Assert.Equal(ChecksumVerdict.Verified, checksums.Verify("cli-linux-arm64", "656129ea72017c1c34069fb22d4ca293d2f4ed58da0f1d06dcb1b168e4ca87bc").Verdict);
        Assert.Equal(ChecksumVerdict.Verified, checksums.Verify("cli-linux-x64", "7f1ec40a40b109d23808c3d1e6e82b3baca043ed5cfa67843ce3ae974afb6e14").Verdict);
    }

    [Fact]
    public void A_duplicated_name_keeps_the_first_digest()
    {
        // An appended line must not be able to override a published one.
        var manifest = RealManifest + "\n" + new string('f', 64) + "  cli-osx-arm64";

        var result = ReleaseChecksums.Parse(manifest).Verify("cli-osx-arm64", new string('f', 64));

        Assert.Equal(ChecksumVerdict.Mismatch, result.Verdict);
    }

    [Fact]
    public async Task The_computed_digest_is_lower_case_hex_matching_sha256sum()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bella-checksum-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(path, "abc", TestContext.Current.CancellationToken);

        try
        {
            // The published SHA-256 of "abc" — pins the encoding, not just self-consistency.
            Assert.Equal(
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                await ReleaseChecksums.ComputeFileSha256Async(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
