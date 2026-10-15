using System.Text.Json;
using BellaBaxter.Client;

namespace BellaCli.Services;

public record BellaConfig(
    string ApiUrl = BellaConfig.DefaultApiUrl,
    // Advisory clients-1 follow-up (cli-bella-url), security team 2026-10-10 — origins the user has
    // confirmed once, so a server named only by a working-directory .bella is used without asking again.
    // Normalized (ConfigService.OriginOf). Absent from config.json until the first one is trusted.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string[]? TrustedOrigins = null
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

    /// <summary>Where this instance saves; null (tests) means it never does.</summary>
    private readonly string? _configFile = ConfigFile;

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

    /// <summary>
    /// Tests only: a service whose machine default is <paramref name="machineDefault"/> instead of the
    /// real <c>config.json</c>. It saves only to <paramref name="configFile"/>, and never to the real one.
    /// </summary>
    internal ConfigService(BellaConfig machineDefault, string? configFile = null)
    {
        _cache = machineDefault;
        _configFile = configFile;
    }

    /// <summary>
    /// Advisory clients-1 follow-up — asks the operator whether to trust a server that only a
    /// working-directory <c>.bella</c> names. Returns true (trust it, remembered), false (no), or null when
    /// there is nobody to ask (stdin is not a terminal, or the output is machine-readable). Unset means
    /// the same as null: a non-interactive process never waits for an answer.
    /// </summary>
    public Func<UntrustedBellaServer, bool?>? OriginConfirmer { get; set; }

    /// <summary>
    /// Origins that already have a stored credential bound to them (advisory clients-1's origin binding).
    /// Such an origin was reached on the user's own choice, so a <c>.bella</c> naming it asks nothing new.
    /// </summary>
    public Func<IEnumerable<string>>? StoredCredentialOrigins { get; set; }

    /// <summary>What was decided this run, per origin, so a command reading the address twice asks once.</summary>
    private readonly Dictionary<string, bool> _decided = new(StringComparer.Ordinal);

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
    /// <para><b>Advisory clients-1 follow-up (cli-bella-url), security team 2026-10-10 — a server named only
    /// by a working-directory <c>.bella</c> is not used until the user has trusted it.</b> A <c>.bella</c>
    /// arrives with every clone, so it could point <c>bella login</c> (and every other command) at a lookalike
    /// whose login page phishes the user. An address from <c>BELLA_BAXTER_URL</c> or <c>config.json</c> is the
    /// user's own choice; one from a <c>.bella</c> is used only when its origin is known (see
    /// <see cref="EnsureBellaServerIsKnown"/>). This getter is the ONE place that rule runs, so every command
    /// gets it; <c>BellaFileUrlIsReadOnlyInConfigServiceTests</c> fails on any other reader of the file's <c>url</c>.</para>
    /// <para>Commands that only DISPLAY the address (<c>config show</c>, <c>context show</c>) read
    /// <see cref="ApiUrlAsConfigured"/> instead, so an operator can still see the value that is being
    /// refused and where it came from.</para>
    /// </remarks>
    public string ApiUrl
    {
        get
        {
            if (ApiUrlProblem is { } problem)
                throw new InsecureApiAddressException(problem);

            var url = ApiUrlAsConfigured;
            if (ApiUrlSource == ".bella")
                EnsureBellaServerIsKnown(url);
            return url;
        }
    }

    /// <summary>
    /// Resolves the server NOW — running the <c>.bella</c> trust check, which may ask the operator — and
    /// discards the address. Call it before opening a status display (<c>IOutputWriter.StatusAsync</c>)
    /// when nothing earlier in the command has read <see cref="ApiUrl"/> (#1317): Spectre cannot run a
    /// prompt while a spinner is live, so the FIRST read of the address must happen outside it. The
    /// answer is remembered for the run, so the reads inside the spinner then ask nothing.
    /// </summary>
    public void ResolveServer() => _ = ApiUrl;

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
    private static string? ReadUrlFromNearestBellaFile() => ReadUrlFromNearestBellaFile(out _);

    private static string? ReadUrlFromNearestBellaFile(out string? bellaPath)
    {
        bellaPath = null;
        try
        {
            var path = KeyContextService.FindBellaFile(Directory.GetCurrentDirectory());
            if (path is null)
                return null;
            bellaPath = path;

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

    // ── Trust in a server a .bella names (advisory clients-1 follow-up, cli-bella-url) ──────────

    /// <summary>The origins the user has confirmed, normalized.</summary>
    public IReadOnlyList<string> TrustedOrigins => _cache.TrustedOrigins ?? [];

    /// <summary>
    /// True when <paramref name="origin"/> is one the user already chose: <c>config.json</c>'s server, an
    /// origin a stored credential is bound to, or one confirmed before.
    /// </summary>
    public bool IsKnownOrigin(string origin) => KnownBecause(origin) != OriginKnownBecause.None;

    /// <summary>Every reason <paramref name="origin"/> is known (<see cref="IsKnownOrigin"/>); None when it is not.</summary>
    public OriginKnownBecause KnownBecause(string origin)
    {
        var because = OriginKnownBecause.None;
        if (string.Equals(origin, OriginOf(MachineDefaultApiUrl), StringComparison.Ordinal))
            because |= OriginKnownBecause.ConfiguredServer;
        if (TrustedOrigins.Contains(origin, StringComparer.Ordinal))
            because |= OriginKnownBecause.Trusted;
        if ((StoredCredentialOrigins?.Invoke() ?? []).Contains(origin, StringComparer.Ordinal))
            because |= OriginKnownBecause.StoredCredential;
        return because;
    }

    /// <summary>
    /// Refuses, before anything is sent or a browser is opened, an address that came from a <c>.bella</c>
    /// whose origin is not known. With a terminal to ask, asks once (default No) and remembers a yes; with
    /// none, refuses and names the ways out. Throws <see cref="UntrustedBellaServerException"/>.
    /// </summary>
    private void EnsureBellaServerIsKnown(string url)
    {
        var origin = OriginOf(url);
        if (origin is not null && IsKnownOrigin(origin))
            return;

        var key = origin ?? url;
        if (_decided.TryGetValue(key, out var accepted))
        {
            if (accepted)
                return;
            throw new UntrustedBellaServerException(Declined(key));
        }

        ReadUrlFromNearestBellaFile(out var bellaPath);
        var server = new UntrustedBellaServer(key, bellaPath ?? ".bella", OriginOf(MachineDefaultApiUrl) ?? MachineDefaultApiUrl);

        var answer = origin is null ? null : OriginConfirmer?.Invoke(server);
        if (answer is true)
        {
            TrustOrigin(origin!);
            _decided[key] = true;
            return;
        }

        if (answer is false)
        {
            _decided[key] = false;
            throw new UntrustedBellaServerException(Declined(key));
        }

        throw new UntrustedBellaServerException(
            $"The .bella at {server.BellaFile} names {server.Origin} as the Bella server, but you have not trusted "
            + $"that server on this machine (your configured server is {server.ConfiguredServer}). A .bella arrives "
            + "with any repository you clone, so its server is used only after you confirm it, and nothing was sent. "
            + $"If it is yours, run `bella config trust {server.Origin}` once; otherwise set BELLA_BAXTER_URL (or pass "
            + "--url/--api-url where a command takes one) to the server you mean.");
    }

    private static string Declined(string origin) =>
        $"You did not trust {origin}, so the .bella that names it was not followed and nothing was sent. "
        + "Set BELLA_BAXTER_URL (or run `bella config trust` for it) if you meant it, or edit the .bella.";

    /// <summary>
    /// Remembers <paramref name="url"/>'s origin as trusted and returns it normalized. The same https rule
    /// as the address itself applies: an origin no command would use is not worth trusting.
    /// </summary>
    public string TrustOrigin(string url)
    {
        var origin = OriginOf(url) ?? throw new ArgumentException($"'{url}' is not an http(s) address.", nameof(url));
        if (ProblemFor(origin, "bella config trust") is { } problem)
            throw new InsecureApiAddressException(problem);

        if (!TrustedOrigins.Contains(origin, StringComparer.Ordinal))
        {
            _cache = _cache with { TrustedOrigins = [.. TrustedOrigins, origin] };
            Save();
        }
        return origin;
    }

    /// <summary>Forgets a trusted origin; returns the normalized origin and whether it was in the list.</summary>
    public (string Origin, bool Removed) UntrustOrigin(string url)
    {
        var origin = OriginOf(url) ?? throw new ArgumentException($"'{url}' is not an http(s) address.", nameof(url));
        var kept = TrustedOrigins.Where(o => !string.Equals(o, origin, StringComparison.Ordinal)).ToArray();
        var removed = kept.Length != TrustedOrigins.Count;
        if (removed)
        {
            _cache = _cache with { TrustedOrigins = kept.Length == 0 ? null : kept };
            Save();
            _decided.Remove(origin);
        }
        return (origin, removed);
    }

    // ── Credential origin binding (advisory clients-1) ──────────────────────────────────────────

    /// <summary>
    /// The machine-wide default server, from <c>config.json</c>: the address the operator chose on
    /// THIS machine, as opposed to one a repository's <c>.bella</c> names.
    /// </summary>
    public string MachineDefaultApiUrl => _cache.ApiUrl;

    /// <summary>
    /// The origin (<c>scheme://host[:port]</c>, lower-case, default port omitted) of
    /// <paramref name="url"/>, or null when it is not an absolute http(s) address. Two addresses name
    /// the same server exactly when their origins are equal: the path, a trailing slash and the case of
    /// the host do not change where a credential goes.
    /// </summary>
    public static string? OriginOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return null;

        var host = uri.HostNameType == UriHostNameType.IPv6
            ? uri.Host.ToLowerInvariant()
            : uri.IdnHost.ToLowerInvariant();
        return uri.IsDefaultPort ? $"{uri.Scheme}://{host}" : $"{uri.Scheme}://{host}:{uri.Port}";
    }

    /// <summary>
    /// The address to present a STORED credential to (the tokens of <c>bella login</c>, or an API key
    /// it stored): <see cref="ApiUrl"/>, but only when that is the server the credential was obtained
    /// from. Otherwise <see cref="CredentialOriginException"/>, before anything is sent.
    /// </summary>
    /// <remarks>
    /// <para><b>Why.</b> The CLI keeps one login, and the server it talks to can come from the nearest
    /// <c>.bella</c>, a file that arrives with every repository you clone. Without this, a repository
    /// naming another server received your bearer and refresh tokens on the first command run in it.
    /// The rule holds whatever supplied the address, an environment variable included: a stored
    /// credential is of no use to a server that did not issue it, so sending it there only exposes it.</para>
    /// <para><b>A credential stored before origins were recorded</b> (<paramref name="recordedOrigin"/>
    /// null) is bound to <see cref="MachineDefaultApiUrl"/>, the server <c>bella login</c> uses when
    /// nothing overrides it. Failing closed costs one <c>bella login</c> for someone who logged in
    /// through an override; guessing the other way would keep sending legacy tokens to whatever a
    /// <c>.bella</c> names.</para>
    /// </remarks>
    public string ApiUrlForStoredCredential(string? recordedOrigin) =>
        StoredCredentialMayGoTo(ApiUrl, ApiUrlSource, recordedOrigin);

    /// <summary>
    /// The same rule for an address a command took from somewhere other than <see cref="ApiUrl"/> (a
    /// <c>--api-url</c> flag): returns <paramref name="url"/> when it is the stored credential's server.
    /// </summary>
    public string StoredCredentialMayGoTo(string url, string source, string? recordedOrigin)
    {
        var credentialOrigin = recordedOrigin ?? OriginOf(MachineDefaultApiUrl);
        var target = OriginOf(url);

        if (target is not null && string.Equals(target, credentialOrigin, StringComparison.Ordinal))
            return url;

        throw new CredentialOriginException(
            $"Your stored Bella login belongs to {credentialOrigin ?? "an unknown server"}, but this command would "
            + $"send it to {target ?? url} (from {source}). It was not sent. "
            + "If you meant to use that server, run `bella login` here to log in to it; otherwise point the CLI "
            + "back with `bella config set-server` or BELLA_BAXTER_URL.");
    }

    /// <summary>
    /// The address to present a credential the ENVIRONMENT supplied (<c>BELLA_BAXTER_API_KEY</c>,
    /// <c>BELLA_BAXTER_ACCESS_TOKEN</c>, a workload's OIDC token) to.
    /// </summary>
    /// <remarks>
    /// Such a credential records no origin, so the rule is about who chose the address: an environment
    /// variable or <c>config.json</c> is the operator's own choice and is used as is; a <c>.bella</c> is
    /// the repository's, and is used only when it names the machine's own default server. A pipeline
    /// that targets another server says so with <c>BELLA_BAXTER_URL</c> (the setup action exports it),
    /// which also wins over <c>.bella</c>.
    /// </remarks>
    public string ApiUrlForSuppliedCredential()
    {
        var url = ApiUrl;
        if (ApiUrlSource != ".bella")
            return url;

        var target = OriginOf(url);
        var machine = OriginOf(MachineDefaultApiUrl);
        if (target is not null && string.Equals(target, machine, StringComparison.Ordinal))
            return url;

        throw new CredentialOriginException(
            $"The .bella in this directory names {target ?? url}, which is not this machine's configured server "
            + $"({machine ?? MachineDefaultApiUrl}). A credential from the environment is not sent to a server a "
            + "repository chose. It was not sent. If you meant that server, set BELLA_BAXTER_URL to it "
            + "(an explicit override) or run `bella config set-server`.");
    }

    /// <summary>The address to present <paramref name="key"/> to (see the two rules above).</summary>
    public string ApiUrlFor(StoredApiKey key) =>
        key.FromEnvironment ? ApiUrlForSuppliedCredential() : ApiUrlForStoredCredential(key.Origin);

    /// <summary>The address to present <paramref name="tokens"/> to.</summary>
    public string ApiUrlFor(StoredTokens tokens) => ApiUrlForStoredCredential(tokens.Origin);

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
        if (_configFile is null)
            return;

        // Written beside the file and moved over it, so a crash or a second `bella` never leaves a
        // half-written config.json (which Load() would read as "nothing configured" and so forget the
        // trusted origins along with the server).
        var json = JsonSerializer.Serialize(_cache, JsonOptions);
        var temp = $"{_configFile}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, _configFile, overwrite: true);
    }
}

/// <summary>Why a server a <c>.bella</c> names is followed without asking (<see cref="ConfigService.KnownBecause"/>).</summary>
[Flags]
public enum OriginKnownBecause
{
    None = 0,
    /// <summary><c>config.json</c>'s own server.</summary>
    ConfiguredServer = 1,
    /// <summary>In <c>trustedOrigins</c> (<c>bella config trust</c>).</summary>
    Trusted = 2,
    /// <summary>A stored login or API key is bound to it.</summary>
    StoredCredential = 4,
}
