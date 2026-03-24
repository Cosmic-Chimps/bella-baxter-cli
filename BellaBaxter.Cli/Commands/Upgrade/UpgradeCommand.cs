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
    private const string GitHubReleasesUrl =
        "https://api.github.com/repos/cosmic-chimps/bella-baxter/releases/latest";

    public class Settings : CommandSettings
    {
        [CommandOption("--check")]
        [Description("Only check for updates, do not install")]
        public bool CheckOnly { get; set; }

        [CommandOption("--version <version>")]
        [Description("Install a specific version (e.g. 1.2.3)")]
        public string? Version { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
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
                var tagUrl = GitHubReleasesUrl.Replace("/latest", $"/tags/v{settings.Version.TrimStart('v')}");
                release = await http.GetFromJsonAsync<GitHubRelease>(tagUrl, ct);
            }
            else
            {
                release = await http.GetFromJsonAsync<GitHubRelease>(GitHubReleasesUrl, ct);
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

        // Download and replace current binary
        var currentExe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Cannot determine current executable path.");

        AnsiConsole.MarkupLine($"[dim]Downloading {Markup.Escape(asset.Name!)} ...[/]");

        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "bella-cli");

            await AnsiConsole.Progress()
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask($"Downloading v{latestVersion}");
                    var tempFile = currentExe + ".new";

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

                    dest.Close();

                    // On Unix make executable
                    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        var chmod = Process.Start("chmod", $"+x {tempFile}");
                        chmod?.WaitForExit();
                    }

                    // Atomic replace: rename old → .bak, new → current
                    var bakFile = currentExe + ".bak";
                    if (File.Exists(bakFile)) File.Delete(bakFile);
                    File.Move(currentExe, bakFile, overwrite: true);
                    File.Move(tempFile, currentExe, overwrite: true);
                    if (File.Exists(bakFile)) File.Delete(bakFile);
                });

            output.WriteSuccess($"bella upgraded to v{latestVersion}!");
            output.WriteInfo("Restart your shell or run 'bella --version' to confirm.");
            return 0;
        }
        catch (Exception ex)
        {
            output.WriteError($"Upgrade failed: {ex.Message}");
            return 1;
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
