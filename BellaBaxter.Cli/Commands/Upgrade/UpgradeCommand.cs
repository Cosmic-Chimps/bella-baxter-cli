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

        // #1051 — provenance: the manifest must carry a signature by the pinned release key, as the installers
        // require. Located before anything is downloaded, for the same reason as the manifest.
        var signatureAsset = release.Assets?.FirstOrDefault(a =>
            string.Equals(a.Name, ReleaseSignature.SignatureAssetName, StringComparison.OrdinalIgnoreCase));
        var skipSignature = ReleaseSignature.InsecureSkipRequested;

        if (signatureAsset?.BrowserDownloadUrl == null && !skipSignature)
        {
            output.WriteError(
                $"Release {latestVersion} publishes no {ReleaseSignature.SignatureAssetName}, so it cannot be verified as published by Cosmic Chimps.");
            output.WriteInfo("Refusing to replace the current binary.");
            WriteSignatureRefusalHint();
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

            // Verify the manifest's signature BEFORE spending 60 MB on the binary: a refusal here leaves nothing to
            // undo. The manifest is checked as the exact bytes downloaded, written beside its signature.
            if (skipSignature)
            {
                output.WriteWarning(
                    $"{ReleaseSignature.InsecureSkipVariable}=1 — the GPG signature is NOT checked. Only the SHA-256 checksum is " +
                    "verified: that proves the download is intact, NOT that Cosmic Chimps published it. Do not use this outside air-gapped setups.");
            }
            else
            {
                var verdict = await VerifyManifestSignatureAsync(http, manifest, signatureAsset!.BrowserDownloadUrl!, ct);
                if (verdict != SignatureVerdict.Verified)
                {
                    output.WriteError(verdict switch
                    {
                        SignatureVerdict.NoSignature => $"{ReleaseSignature.SignatureAssetName} for {latestVersion} could not be downloaded.",
                        SignatureVerdict.GpgMissing => "gpg is required to verify the release signature and was not found. Install GnuPG and retry.",
                        SignatureVerdict.KeyImportFailed => "gpg could not import the embedded Cosmic Chimps signing key; it may be too old or broken.",
                        _ => $"GPG signature verification FAILED for {latestVersion}: checksums.txt is not signed by the Cosmic Chimps " +
                             $"release key ({ReleaseSignature.PinnedFingerprint}). This may indicate tampering.",
                    });
                    output.WriteInfo("The current binary has not been modified.");
                    WriteSignatureRefusalHint();
                    return 1;
                }
                output.WriteInfo($"GPG signature verified (key {ReleaseSignature.PinnedFingerprint}).");
            }

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
        }
        catch (Exception ex)
        {
            TryDelete(tempFile);
            output.WriteError($"Upgrade failed: {ex.Message}");
            return 1;
        }

        // #1238 — from the swap on, this process's executable path holds ANOTHER binary, and a single-file
        // bundle reads every not-yet-loaded assembly from that path (see RunningBinary). So everything that
        // could load one happens first: the HttpClient and streams above are already disposed, and the rest
        // of the reference closure is loaded here. A failed swap restores the old binary, so it is reported
        // like any other failure.
        RunningBinary.PreloadReferencedAssemblies();

        try
        {
            RunningBinary.Replace(currentExe, tempFile);
        }
        catch (Exception ex)
        {
            TryDelete(tempFile);
            output.WriteError($"Upgrade failed: {ex.Message}");
            return 1;
        }

        // The upgrade HAS happened. Say so, then end the process here rather than return into the command
        // framework and host shutdown, which could still reach for an assembly. A line that cannot be printed
        // does not undo the upgrade, so it never turns the exit code into a failure.
        try
        {
            output.WriteSuccess($"bella upgraded to v{latestVersion}!");
            output.WriteInfo("Restart your shell or run 'bella --version' to confirm.");
            Console.Out.Flush();
        }
        catch
        {
            // See above.
        }
        Environment.Exit(0);
        return 0;
    }

    /// <summary>
    /// Downloads the detached signature and checks it over the manifest bytes already downloaded. Both go to a
    /// private temp directory that is removed afterwards.
    /// </summary>
    private static async Task<SignatureVerdict> VerifyManifestSignatureAsync(
        HttpClient http, string manifest, string signatureUrl, CancellationToken ct)
    {
        var dir = Directory.CreateTempSubdirectory("bella-upgrade-");
        try
        {
            var manifestPath = Path.Combine(dir.FullName, ReleaseSource.ChecksumsAssetName);
            var signaturePath = Path.Combine(dir.FullName, ReleaseSignature.SignatureAssetName);
            await File.WriteAllTextAsync(manifestPath, manifest, ct);
            try
            {
                await File.WriteAllBytesAsync(signaturePath, await http.GetByteArrayAsync(signatureUrl, ct), ct);
            }
            catch (HttpRequestException)
            {
                return SignatureVerdict.NoSignature;
            }
            return await ReleaseSignature.VerifyAsync(manifestPath, signaturePath, ct);
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch { /* best effort */ }
        }
    }

    private void WriteSignatureRefusalHint() =>
        output.WriteInfo(
            $"If you cannot verify signatures (an air-gapped mirror without the .asc, say), you may set " +
            $"{ReleaseSignature.InsecureSkipVariable}=1 to upgrade on the SHA-256 checksum alone. That proves the download " +
            "is intact, NOT that Cosmic Chimps published it.");

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
