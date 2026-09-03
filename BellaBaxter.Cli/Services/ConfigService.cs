using System.Text.Json;

namespace BellaCli.Services;

public record BellaConfig(
    string ApiUrl = BellaConfig.DefaultApiUrl
)
{
    /// <summary>
    /// The hosted (SaaS) origin used when nothing is configured. ONE definition: this literal used to
    /// be copy-pasted into ShellOpenCommand, EnvCommand and McpCommand, which quietly baked a
    /// SaaS-only assumption into a binary that also ships to self-hosted installs — where the
    /// configured origin is the operator's own host (and, when the PKI topology is deployed, the
    /// GATEWAY origin `gw.<domain>`, since the certificates/scout subtrees exist only there).
    /// </summary>
    public const string DefaultApiUrl = "https://api.bella-baxter.io";
}

public class ConfigService
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config",
        "bella-cli"
    );

    private static readonly string ConfigFile = Path.Combine(ConfigDir, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private BellaConfig _cache = new();

    public ConfigService()
    {
        Directory.CreateDirectory(ConfigDir);
        _cache = Load();
    }

    public BellaConfig Config => _cache;

    /// <summary>
    /// The server this CLI talks to, most specific source first:
    ///
    /// <list type="number">
    ///   <item><c>BELLA_BAXTER_URL</c> — an explicit override always wins (CI, one-off commands).</item>
    ///   <item><c>BAXTER_URL</c> — deprecated alias, kept for compatibility.</item>
    ///   <item>the nearest <c>.bella</c> file's <c>url</c> — the DIRECTORY's server.</item>
    ///   <item><c>~/.config/bella-cli/config.json</c> — the machine-wide default.</item>
    /// </list>
    ///
    /// <para>The directory step exists because a project+environment slug does not identify a
    /// deployment: <c>nginx-rotation/dev</c> means one thing on the hosted service and another on a
    /// self-hosted box, and both can be checked out side by side. A <c>.bella</c> that names the
    /// project but not the server is therefore ambiguous, and the ambiguity was resolved by whatever
    /// happened to be exported in that shell — which is why the URL had to be re-exported per session.
    /// Recording it alongside the context makes the context complete.</para>
    /// </summary>
    public string ApiUrl =>
        Environment.GetEnvironmentVariable("BELLA_BAXTER_URL")?.TrimEnd('/')
        ?? Environment.GetEnvironmentVariable("BAXTER_URL")?.TrimEnd('/')   // deprecated
        ?? ReadUrlFromNearestBellaFile()?.TrimEnd('/')
        ?? _cache.ApiUrl;

    /// <summary>Where <see cref="ApiUrl"/> came from — surfaced by <c>bella context show</c>.</summary>
    public string ApiUrlSource =>
        Environment.GetEnvironmentVariable("BELLA_BAXTER_URL") is { Length: > 0 }
            ? "BELLA_BAXTER_URL"
            : Environment.GetEnvironmentVariable("BAXTER_URL") is { Length: > 0 }
                ? "BAXTER_URL (deprecated)"
                : ReadUrlFromNearestBellaFile() is not null
                    ? ".bella"
                    : "config.json";

    /// <summary>
    /// Reads <c>url = "…"</c> from the nearest <c>.bella</c>, walking up from the working directory
    /// (same search as the project/environment context, so they always agree on which file wins).
    /// Any read or parse problem yields null so a malformed file degrades to the machine default
    /// rather than breaking every command.
    /// </summary>
    private static string? ReadUrlFromNearestBellaFile()
    {
        try
        {
            var path = KeyContextService.FindBellaFile(Directory.GetCurrentDirectory());
            if (path is null)
                return null;

            foreach (var line in File.ReadAllLines(path))
            {
                var trimmed = line.TrimStart();
                if (!trimmed.StartsWith("url", StringComparison.OrdinalIgnoreCase))
                    continue;

                var eq = trimmed.IndexOf('=');
                if (eq < 0)
                    continue;

                var value = trimmed[(eq + 1)..].Trim().Trim('"').Trim();
                if (value.Length > 0)
                    return value;
            }
        }
        catch
        {
            // Unreadable/malformed .bella must not take the CLI down with it.
        }

        return null;
    }

    public void SetApiUrl(string url)
    {
        _cache = _cache with { ApiUrl = url };
        Save();
    }

    private BellaConfig Load()
    {
        if (!File.Exists(ConfigFile))
            return new BellaConfig();

        try
        {
            var json = File.ReadAllText(ConfigFile);
            return JsonSerializer.Deserialize<BellaConfig>(json, JsonOptions) ?? new BellaConfig();
        }
        catch
        {
            return new BellaConfig();
        }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_cache, JsonOptions);
        File.WriteAllText(ConfigFile, json);
    }
}
