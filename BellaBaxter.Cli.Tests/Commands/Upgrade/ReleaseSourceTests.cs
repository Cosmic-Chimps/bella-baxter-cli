using BellaCli.Commands.Upgrade;

namespace BellaBaxter.Cli.Tests.Commands.Upgrade;

/// <summary>
/// #637 — <c>bella upgrade</c> asked the monorepo, which has never published a release.
/// </summary>
/// <remarks>
/// Self-service upgrade had never worked for any published version, and nothing said so: the command
/// reports "Failed to fetch release info" whether the repository is wrong, private, or simply has no
/// release yet. From inside the CLI those are indistinguishable, so the address is pinned here rather
/// than left to be re-derived by whoever next edits this file.
/// </remarks>
public class ReleaseSourceTests
{
    [Fact]
    public void Releases_are_read_from_the_public_cli_repo_not_the_monorepo()
    {
        Assert.Equal("Cosmic-Chimps/bella-baxter-cli", ReleaseSource.Repository);

        // Asserted on the built URL too: a correct constant assembled into the wrong path is the same
        // 404 to an operator.
        Assert.Equal(
            "https://api.github.com/repos/Cosmic-Chimps/bella-baxter-cli/releases/latest",
            ReleaseSource.LatestUrl);

        Assert.DoesNotContain("repos/cosmic-chimps/bella-baxter/", ReleaseSource.LatestUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("0.1.1-preview.101")]
    [InlineData("v0.1.1-preview.101")]
    public void An_explicit_version_resolves_to_that_tag_with_exactly_one_v(string requested)
    {
        // `--version v1.2.3` and `--version 1.2.3` must land on the same tag. The old code reached the
        // tag URL by string-replacing "/latest" out of the latest URL, which works until the base
        // changes shape and then fails as a 404 nobody can attribute.
        Assert.Equal(
            "https://api.github.com/repos/Cosmic-Chimps/bella-baxter-cli/releases/tags/v0.1.1-preview.101",
            ReleaseSource.TagUrl(requested));
    }
}
