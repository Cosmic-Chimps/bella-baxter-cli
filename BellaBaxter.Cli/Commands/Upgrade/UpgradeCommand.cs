using BellaCli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BellaCli.Commands.Upgrade;

public class UpgradeCommand(IOutputWriter output) : AsyncCommand<UpgradeCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandOption("--check")]
        [Description("Only check for updates, do not install")]
        public bool CheckOnly { get; set; }

        [CommandOption("--version <version>")]
        [Description("Install a specific version (e.g. 1.2.3)")]
        public string? Version { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var currentVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0";

        // Strip build metadata
        var dashIdx = currentVersion.IndexOf('+');
        if (dashIdx > 0) currentVersion = currentVersion[..dashIdx];

        output.WriteInfo($"Current version: {currentVersion}");

        GitHubRelease? release;
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "bella-cli");
            http.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");

            if (settings.Version != null)
            {
                release = await http.GetFromJsonAsync<GitHubRelease>(ReleaseSource.TagUrl(settings.Version), ct);
            }
            else
            {
                release = await http.GetFromJsonAsync<GitHubRelease>(ReleaseSource.LatestUrl, ct);
            }
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to fetch release info: {ex.Message}");
            return 1;
        }

        if (release == null)
        {
            output.WriteError("No release found.");
            return 1;
        }

        var latestVersion = release.TagName?.TrimStart('v') ?? "0.0.0";
        output.WriteInfo($"Latest version:  {latestVersion}");

        if (string.Equals(currentVersion, latestVersion, StringComparison.OrdinalIgnoreCase))
        {
            output.WriteSuccess("bella is already up to date.");
            return 0;
        }

        if (settings.CheckOnly)
        {
            AnsiConsole.MarkupLine($"[yellow]Update available: {currentVersion} → {latestVersion}[/]");
            AnsiConsole.MarkupLine("[dim]Run 'bella upgrade' to install.[/]");
            return 0;
        }

        // Find the right asset for the current platform
        var rid = GetRid();
        var assetName = $"cli-{rid}";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) assetName += ".exe";

        var asset = release.Assets?.FirstOrDefault(a =>
            a.Name?.StartsWith(assetName, StringComparison.OrdinalIgnoreCase) == true);

        if (asset?.BrowserDownloadUrl == null)
        {
            output.WriteError($"No binary found for platform '{rid}' in release {latestVersion}.");
            output.WriteInfo($"Available assets: {string.Join(", ", release.Assets?.Select(a => a.Name) ?? [])}");
            output.WriteInfo($"You can download manually from: {release.HtmlUrl}");
            return 1;
        }

        // The manifest is located BEFORE anything is downloaded: if the release cannot be verified there
        // is no point spending 60 MB to find out, and refusing here keeps the running binary untouched.
        var checksumsAsset = release.Assets?.FirstOrDefault(a =>
            string.Equals(a.Name, ReleaseSource.ChecksumsAssetName, StringComparison.OrdinalIgnoreCase));

        if (checksumsAsset?.BrowserDownloadUrl == null)
        {
            output.WriteError(
                $"Release {latestVersion} publishes no {ReleaseSource.ChecksumsAssetName}, so the download cannot be verified.");
            output.WriteInfo($"Refusing to replace the current binary. Download manually from: {release.HtmlUrl}");
            return 1;
        }

        // Download and replace current binary
        var currentExe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Cannot determine current executable path.");

        AnsiConsole.MarkupLine($"[dim]Downloading {Markup.Escape(asset.Name!)} ...[/]");

        var tempFile = currentExe + ".new";

        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "bella-cli");

            var manifest = await http.GetStringAsync(checksumsAsset.BrowserDownloadUrl, ct);

            await AnsiConsole.Progress()
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask($"Downloading v{latestVersion}");
                    task.Tag = asset.BrowserDownloadUrl;

                    using var response = await http.GetAsync(asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                    response.EnsureSuccessStatusCode();

                    var total = response.Content.Headers.ContentLength ?? -1L;
                    await using var dest = File.Create(tempFile);
                    await using var src = await response.Content.ReadAsStreamAsync(ct);

                    var buffer = new byte[81920];
                    long downloaded = 0;
                    int read;
                    while ((read = await src.ReadAsync(buffer, ct)) > 0)
                    {
                        await dest.WriteAsync(buffer.AsMemory(0, read), ct);
                        downloaded += read;
                        if (total > 0) task.Value = downloaded * 100.0 / total;
                    }
                    task.Value = 100;
                });

            // Verify BEFORE the replace, and outside the progress display so a refusal is readable.
            // Everything above this line is reversible by deleting a temp file; everything below it
            // overwrites the binary the operator is currently running.
            var actual = await ReleaseChecksums.ComputeFileSha256Async(tempFile, ct);
            var verification = ReleaseChecksums.Parse(manifest).Verify(asset.Name!, actual);

            if (!verification.IsVerified)
            {
                TryDelete(tempFile);

                if (verification.Verdict == ChecksumVerdict.NotListed)
                {
                    output.WriteError(
                        $"{ReleaseSource.ChecksumsAssetName} for {latestVersion} does not list '{asset.Name}', so the download cannot be verified.");
                }
                else
                {
                    output.WriteError($"Checksum mismatch for '{asset.Name}' — the download does not match the published release.");
                    output.WriteInfo($"expected {verification.Expected}");
                    output.WriteInfo($"actual   {verification.Actual}");
                }

                output.WriteInfo("The current binary has not been modified.");
                return 1;
            }

            // On Unix make executable
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var chmod = Process.Start("chmod", $"+x {tempFile}");
                chmod?.WaitForExit();
            }

            // Atomic replace: rename old -> .bak, new -> current
            var bakFile = currentExe + ".bak";
            if (File.Exists(bakFile)) File.Delete(bakFile);
            File.Move(currentExe, bakFile, overwrite: true);
            File.Move(tempFile, currentExe, overwrite: true);
            if (File.Exists(bakFile)) File.Delete(bakFile);

            output.WriteSuccess($"bella upgraded to v{latestVersion}!");
            output.WriteInfo("Restart your shell or run 'bella --version' to confirm.");
            return 0;
        }
        catch (Exception ex)
        {
            TryDelete(tempFile);
            output.WriteError($"Upgrade failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>A leftover <c>.new</c> would be picked up by nothing, but it is 60 MB of confusion.</summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best effort: failing to tidy up must not mask why the upgrade was refused.
        }
    }

    private static string GetRid()
    {
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx"
            : "linux";

        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => "x64",
        };

        return $"{os}-{arch}";
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    }
}
