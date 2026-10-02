using System.Text.Json;
using BellaBaxter.Client;

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
    /// <remarks>
    /// <para><b>Backlog §2.31 follow-up — https, or plain http to this machine only.</b> Every command
    /// that talks to the server gets its address HERE, so this is where the rule is applied: the value
    /// is refused with <see cref="InsecureApiAddressException"/> when it is plain http to anything but
    /// loopback (<c>localhost</c>, 127.0.0.0/8, <c>::1</c>). Credentials, API keys, tokens and decrypted
    /// secrets travel over it. The rule is <see cref="BellaApiAddress"/>'s — the same one
    /// <c>bella spiffe agent</c> and the .NET SPIFFE SDK apply — and there is no opt-out.</para>
    /// <para>Commands that only DISPLAY the address (<c>config show</c>, <c>context show</c>) read
    /// <see cref="ApiUrlAsConfigured"/> instead, so an operator can still see the value that is being
    /// refused and where it came from.</para>
    /// </remarks>
    public string ApiUrl =>
        ApiUrlProblem is { } problem
            ? throw new InsecureApiAddressException(problem)
            : ApiUrlAsConfigured;

    /// <summary>
    /// The configured address WITHOUT the https rule. For display only; anything that sends a request
    /// uses <see cref="ApiUrl"/>. A guard forbids this in any file that builds an HTTP request.
    /// </summary>
    public string ApiUrlAsConfigured =>
        Environment.GetEnvironmentVariable("BELLA_BAXTER_URL")?.TrimEnd('/')
        ?? Environment.GetEnvironmentVariable("BAXTER_URL")?.TrimEnd('/')   // deprecated
        ?? ReadUrlFromNearestBellaFile()?.TrimEnd('/')
        ?? _cache.ApiUrl;

    /// <summary>Why <see cref="ApiUrlAsConfigured"/> may not be used, naming its source; null when it may.</summary>
    public string? ApiUrlProblem => ProblemFor(ApiUrlAsConfigured, ApiUrlSource);

    /// <summary>
    /// The one wording of the refusal: the rule's sentence, naming WHERE the address came from (an
    /// environment variable, a <c>.bella</c> file, <c>config.json</c>, a flag), because that source is
    /// what the operator has to change.
    /// </summary>
    public static string? ProblemFor(string apiUrl, string source) =>
        BellaApiAddress.Problem(apiUrl, $"The Bella API address (from {source})") is { } problem
            ? problem + " Point it at the server's https:// address (for example `bella config set-server https://…`)."
            : null;

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
        // Refused at SAVE too, so config.json never holds an address every later command would refuse.
        if (ProblemFor(url, "bella config set-server") is { } problem)
            throw new InsecureApiAddressException(problem);

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
