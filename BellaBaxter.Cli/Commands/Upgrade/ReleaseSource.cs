namespace BellaCli.Commands.Upgrade;

/// <summary>
/// The ONE declaration of where CLI releases come from.
/// </summary>
/// <remarks>
/// <para>This pointed at the monorepo (<c>cosmic-chimps/bella-baxter</c>), which has never published a
/// release: the endpoint answers <c>404</c>, so <c>bella upgrade</c> could not deliver anything, ever.
/// Releases are built in <c>bella-baxter-cli</c> — the repo <c>sync-cli.yml</c> subtree-syncs
/// <c>apps/cli-dotnet</c> into, and whose <c>publish.yml</c> attaches the platform artifacts.</para>
///
/// <para>It is a named constant with a test on it (<c>ReleaseSourceTests</c>) because the failure mode is
/// silent from in here: the command reports "Failed to fetch release info" either way, whether the repo
/// is wrong, private, or simply has no release yet. Nothing in the CLI can tell those apart, so the
/// address is pinned rather than trusted.</para>
/// </remarks>
public static class ReleaseSource
{
    /// <summary>The PUBLIC CLI repository, which is where releases actually exist.</summary>
    public const string Repository = "Cosmic-Chimps/bella-baxter-cli";

    /// <summary>The asset every release carries listing each binary's SHA-256 (`sha256sum cli-*`).</summary>
    public const string ChecksumsAssetName = "checksums.txt";

    public static string LatestUrl => $"https://api.github.com/repos/{Repository}/releases/latest";

    /// <summary>
    /// The release for one explicit version. Built from the same base rather than by string-replacing
    /// <c>/latest</c> out of <see cref="LatestUrl"/>, which is the sort of surgery that keeps working
    /// right up until the base changes shape.
    /// </summary>
    public static string TagUrl(string version) =>
        $"https://api.github.com/repos/{Repository}/releases/tags/v{version.TrimStart('v')}";
}
