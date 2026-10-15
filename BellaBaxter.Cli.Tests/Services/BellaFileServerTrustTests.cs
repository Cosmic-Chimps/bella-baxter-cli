using System.Text.Json;
using BellaCli.Commands.Config;
using BellaCli.Infrastructure;
using BellaCli.Services;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// Advisory clients-1 follow-up (cli-bella-url), security team 2026-10-10 — a server that only a
/// working-directory <c>.bella</c> names is not used until the user has trusted it.
/// </summary>
/// <remarks>
/// <para>A <c>.bella</c> arrives with every clone, so its <c>url</c> could point <c>bella login</c> at a
/// look-alike whose login page phishes the user. clients-1 already stopped a stored credential going to
/// the wrong origin; this closes the part it listed as "not covered".</para>
/// <para>Hermetic: the machine default and the config file are injected (never the developer's real
/// <c>config.json</c>), and the tests that reach <c>AuthService</c> use a handler that fails the test if
/// anything is sent. They mutate the working directory and environment variables, so they share the
/// non-parallel collection with the other address tests.</para>
/// </remarks>
[Collection(ApiUrlEnvironmentCollection.Name)]
public class BellaFileServerTrustTests : IDisposable
{
    private const string Saas = "https://api.bella-baxter.io";
    private const string Lookalike = "https://bella-baxter.evil.example";
    private const string Known = "https://bella.example.com";

    private static readonly string[] Variables = ["BELLA_BAXTER_URL", "BAXTER_URL"];

    private readonly string _dir;
    private readonly string _configFile;
    private readonly string _originalCwd;
    private readonly Dictionary<string, string?> _original = new();

    public BellaFileServerTrustTests()
    {
        _originalCwd = Directory.GetCurrentDirectory();
        foreach (var v in Variables)
        {
            _original[v] = Environment.GetEnvironmentVariable(v);
            Environment.SetEnvironmentVariable(v, null);
        }

        _dir = Path.Combine(Path.GetTempPath(), "bella-trust-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configFile = Path.Combine(_dir, "config.json");
        Directory.SetCurrentDirectory(_dir);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalCwd);
        foreach (var (k, v) in _original)
            Environment.SetEnvironmentVariable(k, v);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // macOS reports the temp directory as /private/var/... and Path.GetTempPath() as /var/...
    private string BellaSuffix => Path.Combine(Path.GetFileName(_dir), ".bella");

    private void WriteBella(string url) =>
        File.WriteAllText(Path.Combine(_dir, ".bella"), $"project = \"p\"\nenvironment = \"e\"\nurl = \"{url}\"\n");

    private ConfigService Machine(
        string defaultUrl = Saas,
        string[]? trusted = null,
        Func<UntrustedBellaServer, bool?>? confirmer = null,
        params string[] credentialOrigins) =>
        new(new BellaConfig(defaultUrl, trusted), _configFile)
        {
            OriginConfirmer = confirmer,
            StoredCredentialOrigins = () => credentialOrigins,
        };

    private string[] PersistedTrust() =>
        File.Exists(_configFile)
            ? JsonSerializer.Deserialize<BellaConfig>(File.ReadAllText(_configFile), new JsonSerializerOptions(JsonSerializerDefaults.Web))!
                .TrustedOrigins ?? []
            : [];

    // ── an unknown .bella origin, nobody to ask ─────────────────────────────────────────────

    [Fact]
    public void An_unknown_bella_origin_is_refused_when_there_is_nobody_to_ask()
    {
        WriteBella(Lookalike);
        var asked = false;
        var config = Machine(confirmer: _ => { asked = true; return null; });

        var ex = Assert.Throws<UntrustedBellaServerException>(() => _ = config.ApiUrl);

        Assert.True(asked); // the confirmer is the thing that knows whether there is a terminal
        Assert.Contains(Lookalike, ex.Message, StringComparison.Ordinal);
        Assert.Contains(BellaSuffix, ex.Message, StringComparison.Ordinal);
        Assert.Contains("bella config trust", ex.Message, StringComparison.Ordinal);
        Assert.Contains("BELLA_BAXTER_URL", ex.Message, StringComparison.Ordinal);
        Assert.Empty(PersistedTrust());
    }

    [Fact]
    public void With_no_confirmer_at_all_the_answer_is_refusal_never_a_wait()
    {
        WriteBella(Lookalike);

        Assert.Throws<UntrustedBellaServerException>(() => _ = Machine().ApiUrl);
    }

    [Fact]
    public async Task Login_discovery_is_refused_before_any_request_or_browser()
    {
        // `bella login` reads the address through the same getter, so the prompt/refusal precedes the
        // /api/v1/auth/config probe that names the login page. The handler fails the test if it is hit.
        WriteBella(Lookalike);
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var auth = new AuthService(Machine(), credentials: null!, http);

        await Assert.ThrowsAsync<UntrustedBellaServerException>(
            () => auth.LoginWithBrowserAsync(TestContext.Current.CancellationToken));

        Assert.Empty(handler.Requests);
    }

    // ── an unknown .bella origin, a terminal to ask ─────────────────────────────────────────

    [Fact]
    public void Answering_yes_trusts_the_origin_remembers_it_and_proceeds()
    {
        WriteBella(Lookalike + "/some/path/");
        UntrustedBellaServer? shown = null;
        var config = Machine(confirmer: s => { shown = s; return true; });

        Assert.Equal(Lookalike + "/some/path", config.ApiUrl);

        Assert.NotNull(shown);
        Assert.Equal(Lookalike, shown!.Origin);
        Assert.EndsWith(BellaSuffix, shown.BellaFile, StringComparison.Ordinal);
        Assert.Equal(Saas, shown.ConfiguredServer);
        Assert.Equal([Lookalike], PersistedTrust());

        // ...and a fresh process asks nothing.
        var next = new ConfigService(new BellaConfig(Saas, PersistedTrust()), _configFile) { OriginConfirmer = _ => throw new Xunit.Sdk.XunitException("asked again") };
        Assert.Equal(Lookalike + "/some/path", next.ApiUrl);
    }

    [Fact]
    public async Task Answering_no_aborts_persists_nothing_and_sends_nothing()
    {
        WriteBella(Lookalike);
        var asks = 0;
        var config = Machine(confirmer: _ => { asks++; return false; });
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var auth = new AuthService(config, credentials: null!, http);

        await Assert.ThrowsAsync<UntrustedBellaServerException>(
            () => auth.LoginWithBrowserAsync(TestContext.Current.CancellationToken));
        Assert.Throws<UntrustedBellaServerException>(() => _ = config.ApiUrl);

        Assert.Equal(1, asks); // a declined origin is not asked about again in the same run
        Assert.Empty(handler.Requests);
        Assert.Empty(PersistedTrust());
        Assert.False(File.Exists(_configFile));
    }

    [Fact]
    public void The_question_is_asked_once_however_often_the_address_is_read()
    {
        WriteBella(Lookalike);
        var asks = 0;
        var config = Machine(confirmer: _ => { asks++; return true; });

        _ = config.ApiUrl;
        _ = config.ApiUrl;
        _ = config.ApiUrl;

        Assert.Equal(1, asks);
    }

    // ── origins that are already known ──────────────────────────────────────────────────────

    [Fact]
    public void The_configured_server_needs_no_confirmation_whatever_spelling_the_bella_uses()
    {
        WriteBella("https://API.bella-baxter.io:443/");
        var config = Machine(confirmer: _ => throw new Xunit.Sdk.XunitException("asked"));

        Assert.Equal("https://API.bella-baxter.io:443", config.ApiUrl);
    }

    [Fact]
    public void An_origin_a_stored_credential_is_bound_to_needs_no_confirmation()
    {
        WriteBella(Known);
        var config = Machine(confirmer: _ => throw new Xunit.Sdk.XunitException("asked"), credentialOrigins: Known);

        Assert.Equal(Known, config.ApiUrl);
    }

    [Fact]
    public void A_credential_bound_elsewhere_does_not_vouch_for_the_bella_origin()
    {
        WriteBella(Lookalike);

        Assert.Throws<UntrustedBellaServerException>(() => _ = Machine(credentialOrigins: Known).ApiUrl);
    }

    [Fact]
    public void An_origin_on_the_trusted_list_needs_no_confirmation()
    {
        WriteBella(Known + "/");
        var config = Machine(trusted: [Known], confirmer: _ => throw new Xunit.Sdk.XunitException("asked"));

        Assert.Equal(Known, config.ApiUrl);
    }

    [Fact]
    public void Trust_is_per_origin_not_per_host()
    {
        // Another port, another scheme or a sibling host is another server.
        foreach (var other in new[] { "https://bella.example.com:8443", "https://bella.example.com.evil.example" })
        {
            WriteBella(other);
            Assert.Throws<UntrustedBellaServerException>(() => _ = Machine(trusted: [Known]).ApiUrl);
        }
    }

    // ── addresses the user chose themselves ─────────────────────────────────────────────────

    [Theory]
    [InlineData("BELLA_BAXTER_URL")]
    [InlineData("BAXTER_URL")]
    public void An_environment_override_is_trusted_and_beats_an_untrusted_bella(string variable)
    {
        WriteBella(Lookalike);
        Environment.SetEnvironmentVariable(variable, Known);
        var config = Machine(confirmer: _ => throw new Xunit.Sdk.XunitException("asked"));

        Assert.Equal(Known, config.ApiUrl);
    }

    [Fact]
    public void An_environment_override_naming_a_new_server_needs_no_confirmation_either()
    {
        // BELLA_BAXTER_URL is the way out the refusal names: the user typed it.
        Environment.SetEnvironmentVariable("BELLA_BAXTER_URL", Lookalike);

        Assert.Equal(Lookalike, Machine(confirmer: _ => throw new Xunit.Sdk.XunitException("asked")).ApiUrl);
    }

    [Fact]
    public void Config_json_is_trusted_and_a_context_without_a_url_asks_nothing()
    {
        File.WriteAllText(Path.Combine(_dir, ".bella"), "project = \"p\"\nenvironment = \"e\"\n");

        Assert.Equal(Known, Machine(defaultUrl: Known, confirmer: _ => throw new Xunit.Sdk.XunitException("asked")).ApiUrl);
    }

    [Fact]
    public void An_explicit_api_url_flag_never_reaches_the_resolver()
    {
        // `bella mcp --api-url` returns the flag's value without reading config.ApiUrl, so even with an
        // untrusted .bella in the directory it asks nothing and uses the flag.
        WriteBella(Lookalike);
        var config = Machine(confirmer: _ => throw new Xunit.Sdk.XunitException("asked"));

        Assert.Equal(Known, BellaCli.Commands.Mcp.McpCommand.ResolveApiBase(Known, config));
    }

    // ── the display paths and the credential layer ──────────────────────────────────────────

    [Fact]
    public void Showing_the_address_never_asks()
    {
        WriteBella(Lookalike);
        var config = Machine(confirmer: _ => throw new Xunit.Sdk.XunitException("asked"));

        Assert.Equal(Lookalike, config.ApiUrlAsConfigured);
        Assert.Equal(".bella", config.ApiUrlSource);
    }

    [Fact]
    public void Trusting_a_bella_origin_does_not_unlock_a_credential_bound_elsewhere()
    {
        // The two rules are independent: the user said "yes, talk to that server", but the stored login
        // still goes only where it was obtained.
        WriteBella(Lookalike);
        var config = Machine(confirmer: _ => true);
        var tokens = new StoredTokens("a", "r", DateTimeOffset.UtcNow.AddMinutes(5), Origin: Saas);

        Assert.Throws<CredentialOriginException>(() => config.ApiUrlFor(tokens));
    }

    // ── bella login must not launder an untrusted url (item 6) ──────────────────────────────

    [Fact]
    public void Login_writing_a_bella_leaves_an_untrusted_url_untrusted()
    {
        // `bella login --api-key` at a trusted server merges the key's context into an existing .bella —
        // "its url survives". That must not make the url trusted: the NEXT command still asks/refuses.
        WriteBella(Lookalike);
        var ctx = new KeyContextService.KeyContext("proj", "Proj", "dev", "Dev", "CONSUMER");

        KeyContextService.WriteBellaFile(_dir, ctx, apiUrl: Known);

        var written = File.ReadAllText(Path.Combine(_dir, ".bella"));
        Assert.Contains(Lookalike, written, StringComparison.Ordinal);
        Assert.DoesNotContain(Known, written, StringComparison.Ordinal);
        Assert.Throws<UntrustedBellaServerException>(() => _ = Machine(credentialOrigins: Known).ApiUrl);
        Assert.Empty(PersistedTrust());
    }

    // ── normalisation and persistence ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("HTTPS://Bella.Example.COM/", "https://bella.example.com")]
    [InlineData("https://bella.example.com:443/api/v1", "https://bella.example.com")]
    [InlineData("https://bella.example.com:8443/x", "https://bella.example.com:8443")]
    [InlineData("http://localhost:5522/", "http://localhost:5522")]
    public void Trusting_stores_the_normalised_origin_once(string url, string origin)
    {
        var config = Machine();

        Assert.Equal(origin, config.TrustOrigin(url));
        Assert.Equal(origin, config.TrustOrigin(url + "?again"));

        Assert.Equal([origin], PersistedTrust());
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp")); // written beside and moved, nothing left behind
    }

    [Fact]
    public void Trusting_refuses_what_no_command_would_use()
    {
        Assert.Throws<InsecureApiAddressException>(() => Machine().TrustOrigin("http://bella.lan"));
        Assert.Throws<ArgumentException>(() => Machine().TrustOrigin("not a url"));
    }

    [Fact]
    public void Trusting_keeps_the_configured_server_in_the_file()
    {
        var config = Machine(defaultUrl: Known);
        config.TrustOrigin(Lookalike);

        var json = File.ReadAllText(_configFile);
        Assert.Contains(Known, json, StringComparison.Ordinal);
        Assert.Contains(Lookalike, json, StringComparison.Ordinal);
    }

    // ── bella config trust ──────────────────────────────────────────────────────────────────

    [Fact]
    public void The_trust_command_adds_lists_and_removes()
    {
        var config = Machine();
        var output = new Capture();
        var command = new ConfigTrustCommand(config, output);

        Assert.Equal(0, command.Run(new ConfigTrustCommand.Settings { Url = Known + "/x" }));
        Assert.Contains(output.Lines, l => l.Contains(Known, StringComparison.Ordinal) && l.StartsWith("Trusting", StringComparison.Ordinal));
        Assert.Equal([Known], PersistedTrust());

        Assert.Equal(0, command.Run(new ConfigTrustCommand.Settings { List = true }));
        Assert.Contains(output.Lines, l => l.Contains(Known, StringComparison.Ordinal) && l.Contains("trustedOrigins", StringComparison.Ordinal));

        Assert.Equal(0, command.Run(new ConfigTrustCommand.Settings { Url = Known.ToUpperInvariant(), Remove = true }));
        Assert.Empty(PersistedTrust());
        Assert.Empty(config.TrustedOrigins);

        // forgetting something that was never there is not an error, and says so
        Assert.Equal(0, command.Run(new ConfigTrustCommand.Settings { Url = Known, Remove = true }));
        Assert.Contains(output.Lines, l => l.Contains("not in the trusted list", StringComparison.Ordinal));
    }

    // ── bella config trust --remove: only claim what is true (#1312) ─────────────────────────

    [Fact]
    public void Removing_an_origin_a_stored_login_is_bound_to_does_not_claim_trust_ended()
    {
        var config = Machine(trusted: [Known], credentialOrigins: Known);
        var output = new Capture();

        Assert.Equal(0, new ConfigTrustCommand(config, output).Run(
            new ConfigTrustCommand.Settings { Url = Known, Remove = true }));

        Assert.Empty(PersistedTrust()); // the list entry really is gone
        Assert.True(config.IsKnownOrigin(Known)); // ...and the origin is still followed without asking
        Assert.DoesNotContain(output.Lines, l => l.Contains("No longer trusting", StringComparison.Ordinal));
        Assert.Contains(output.Lines, l => l.Contains("session", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(output.Lines, l => l.Contains("bella logout", StringComparison.Ordinal));
    }

    [Fact]
    public void Removing_the_configured_server_says_it_is_the_configured_server()
    {
        var config = Machine(defaultUrl: Known, trusted: [Known]);
        var output = new Capture();

        Assert.Equal(0, new ConfigTrustCommand(config, output).Run(
            new ConfigTrustCommand.Settings { Url = Known, Remove = true }));

        Assert.True(config.IsKnownOrigin(Known));
        Assert.DoesNotContain(output.Lines, l => l.Contains("No longer trusting", StringComparison.Ordinal));
        Assert.Contains(output.Lines, l => l.Contains("configured server", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(output.Lines, l => l.Contains("bella logout", StringComparison.Ordinal)); // no session here
    }

    [Fact]
    public void Removing_an_origin_nothing_else_binds_says_no_longer_trusting()
    {
        var config = Machine(trusted: [Known]);
        var output = new Capture();

        Assert.Equal(0, new ConfigTrustCommand(config, output).Run(
            new ConfigTrustCommand.Settings { Url = Known, Remove = true }));

        Assert.False(config.IsKnownOrigin(Known));
        Assert.Contains(output.Lines, l => l == $"No longer trusting {Known}");
        Assert.DoesNotContain(output.Lines, l => l.Contains("logout", StringComparison.Ordinal));
    }

    [Fact]
    public void Removing_while_a_session_binds_it_is_reported_in_json_mode_too()
    {
        // The command only talks to the writer, but JSON mode is where a script reads the answer: stdout keeps
        // the (true) success, and the caveat goes to stderr as a warning.
        var config = Machine(trusted: [Known], credentialOrigins: Known);
        var (stdout, stderr) = (new StringWriter(), new StringWriter());
        var (originalOut, originalErr) = (Console.Out, Console.Error);
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            Assert.Equal(0, new ConfigTrustCommand(config, new JsonOutputWriter()).Run(
                new ConfigTrustCommand.Settings { Url = Known, Remove = true }));
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }

        Assert.DoesNotContain("No longer trusting", stdout.ToString() + stderr, StringComparison.Ordinal);
        Assert.Contains("\"success\":true", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("bella logout", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_trust_command_rejects_a_missing_or_unusable_address()
    {
        var config = Machine();
        var output = new Capture();
        var command = new ConfigTrustCommand(config, output);

        Assert.Equal(1, command.Run(new ConfigTrustCommand.Settings()));
        Assert.Equal(1, command.Run(new ConfigTrustCommand.Settings { Url = "not a url" }));
        Assert.Equal(1, command.Run(new ConfigTrustCommand.Settings { Url = "http://bella.lan" }));
        Assert.Empty(PersistedTrust());
    }

    [Fact]
    public void A_trusted_origin_survives_a_reload_of_config_json()
    {
        Machine().TrustOrigin(Known);

        var reloaded = new ConfigService(JsonSerializer.Deserialize<BellaConfig>(
            File.ReadAllText(_configFile), new JsonSerializerOptions(JsonSerializerDefaults.Web))!, _configFile);

        Assert.True(reloaded.IsKnownOrigin(Known));
        Assert.False(reloaded.IsKnownOrigin(Lookalike));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<Uri?> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri);
            throw new InvalidOperationException($"A request was sent to {request.RequestUri}.");
        }
    }

    private sealed class Capture : IOutputWriter
    {
        public List<string> Lines { get; } = [];
        public Task StatusAsync(string message, Func<Task> work) => work();
        public Task StatusAsync(string message, Func<Action<string>, Task> work) => work(_ => { });
        public void WriteObject<T>(T obj) => Lines.Add(JsonSerializer.Serialize(obj));
        public void WriteList<T>(IEnumerable<T> items) => Lines.Add(JsonSerializer.Serialize(items));
        public void WriteTable(string[] headers, IEnumerable<string[]> rows) { }
        public void WriteSuccess(string message) => Lines.Add(message);
        public void WriteError(string message, string? code = null) => Lines.Add(message);
        public void WriteWarning(string message) => Lines.Add(message);
        public void WriteInfo(string message) => Lines.Add(message);
    }
}
