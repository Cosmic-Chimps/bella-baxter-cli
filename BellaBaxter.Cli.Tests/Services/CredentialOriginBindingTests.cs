using BellaCli.Infrastructure;
using BellaCli.Services;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// Advisory clients-1 — the CLI presents a credential only to a server it belongs to. A repository's
/// <c>.bella</c> may still choose the server (the directory-server feature), but a stored login is sent
/// only to the origin it was obtained from, and a credential from the environment never to an address a
/// <c>.bella</c> chose unless that is the machine's own default server.
/// </summary>
/// <remarks>
/// These mutate the working directory and environment variables, so they share the non-parallel
/// collection with the other address tests. The machine default is injected, never read from the
/// developer's real <c>config.json</c>.
/// </remarks>
[Collection(ApiUrlEnvironmentCollection.Name)]
public class CredentialOriginBindingTests : IDisposable
{
    private const string Saas = "https://api.bella-baxter.io";
    private const string SelfHosted = "https://bella.example.com";
    private const string Foreign = "https://collector.example.net";

    private static readonly string[] Variables =
        ["BELLA_BAXTER_URL", "BAXTER_URL", "BELLA_BAXTER_API_KEY", "BELLA_API_KEY", "BELLA_BAXTER_ACCESS_TOKEN"];

    private readonly string _dir;
    private readonly string _originalCwd;
    private readonly Dictionary<string, string?> _original = new();

    public CredentialOriginBindingTests()
    {
        _originalCwd = Directory.GetCurrentDirectory();
        foreach (var v in Variables)
        {
            _original[v] = Environment.GetEnvironmentVariable(v);
            Environment.SetEnvironmentVariable(v, null);
        }

        _dir = Path.Combine(Path.GetTempPath(), "bella-origin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Directory.SetCurrentDirectory(_dir);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalCwd);
        foreach (var (k, v) in _original)
            Environment.SetEnvironmentVariable(k, v);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private void WriteBella(string url) =>
        File.WriteAllText(Path.Combine(_dir, ".bella"), $"project = \"p\"\nenvironment = \"e\"\nurl = \"{url}\"\n");

    private static ConfigService Machine(string defaultUrl = Saas) => new(new BellaConfig(defaultUrl));

    private static StoredTokens Tokens(string? origin) =>
        new("access", "refresh", DateTimeOffset.UtcNow.AddMinutes(5), Origin: origin);

    // ── origin normalisation ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://Bella.Example.com", "https://bella.example.com")]
    [InlineData("https://bella.example.com/", "https://bella.example.com")]
    [InlineData("https://bella.example.com:443/api", "https://bella.example.com")]
    [InlineData("https://bella.example.com:8443", "https://bella.example.com:8443")]
    [InlineData("http://localhost:5522", "http://localhost:5522")]
    [InlineData("http://[::1]:5522", "http://[::1]:5522")]
    [InlineData("ftp://bella.example.com", null)]
    [InlineData("not a url", null)]
    public void An_origin_ignores_case_path_and_the_default_port(string url, string? expected) =>
        Assert.Equal(expected, ConfigService.OriginOf(url));

    // ── stored credentials ──────────────────────────────────────────────────────────────────

    [Fact]
    public void A_bella_file_naming_another_server_does_not_receive_the_stored_login()
    {
        WriteBella(Foreign);
        var config = Machine();

        var ex = Assert.Throws<CredentialOriginException>(() => config.ApiUrlFor(Tokens(Saas)));

        Assert.Contains(Saas, ex.Message, StringComparison.Ordinal);
        Assert.Contains("https://collector.example.net", ex.Message, StringComparison.Ordinal);
        Assert.Contains("from .bella", ex.Message, StringComparison.Ordinal);
        Assert.Contains("bella login", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://bella.example.com")]
    [InlineData("https://BELLA.example.com/")]
    [InlineData("https://bella.example.com:443")]
    public void The_directory_server_still_works_when_it_is_the_server_the_login_belongs_to(string bellaUrl)
    {
        // The feature the .bella url exists for: a checkout of a self-hosted project, logged in to that host.
        WriteBella(bellaUrl);
        var config = Machine(Saas);

        Assert.Equal(bellaUrl.TrimEnd('/'), config.ApiUrlFor(Tokens(SelfHosted)));
    }

    [Fact]
    public void An_environment_variable_does_not_move_a_stored_login_either()
    {
        // The variable is the operator's choice of SERVER, not of whose credential to send there.
        Environment.SetEnvironmentVariable("BELLA_BAXTER_URL", SelfHosted);

        var ex = Assert.Throws<CredentialOriginException>(() => Machine().ApiUrlFor(Tokens(Saas)));
        Assert.Contains("from BELLA_BAXTER_URL", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_login_stored_before_origins_were_recorded_belongs_to_the_machine_default()
    {
        var config = Machine(SelfHosted);

        WriteBella(SelfHosted);
        Assert.Equal(SelfHosted, config.ApiUrlFor(Tokens(origin: null)));

        WriteBella(Foreign);
        Assert.Throws<CredentialOriginException>(() => config.ApiUrlFor(Tokens(origin: null)));
    }

    [Fact]
    public void A_stored_api_key_is_bound_like_the_tokens()
    {
        WriteBella(Foreign);
        var key = new StoredApiKey("id", "secret", "bax-id-secret", Origin: Saas);

        Assert.Throws<CredentialOriginException>(() => Machine().ApiUrlFor(key));
    }

    [Fact]
    public void An_explicit_flag_is_held_to_the_stored_credential_too()
    {
        var config = Machine();

        Assert.Equal(Saas, config.StoredCredentialMayGoTo(Saas, "--api-url", Saas));
        Assert.Throws<CredentialOriginException>(() => config.StoredCredentialMayGoTo(Foreign, "--api-url", Saas));
    }

    // ── credentials from the environment ────────────────────────────────────────────────────

    [Fact]
    public void An_environment_credential_is_not_sent_to_a_server_a_bella_file_chose()
    {
        WriteBella(Foreign);
        var key = new StoredApiKey("id", "secret", "bax-id-secret") { FromEnvironment = true };

        var ex = Assert.Throws<CredentialOriginException>(() => Machine().ApiUrlFor(key));
        Assert.Contains("BELLA_BAXTER_URL", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_environment_credential_follows_an_explicit_override_or_the_machine_default()
    {
        WriteBella(Foreign);
        Environment.SetEnvironmentVariable("BELLA_BAXTER_URL", SelfHosted);
        Assert.Equal(SelfHosted, Machine().ApiUrlForSuppliedCredential());

        Environment.SetEnvironmentVariable("BELLA_BAXTER_URL", null);
        WriteBella(Saas);
        Assert.Equal(Saas, Machine(Saas).ApiUrlForSuppliedCredential());
    }

    // ── nothing is sent ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_refresh_for_another_server_is_refused_before_any_request()
    {
        // The refresh token goes to the token endpoint the API names, so a foreign API would choose
        // where it goes. The handler fails the test if anything is sent at all.
        WriteBella(Foreign);
        using var http = new HttpClient(new NothingMayBeSent());
        var auth = new AuthService(Machine(), credentials: null!, http);

        await Assert.ThrowsAsync<CredentialOriginException>(
            () => auth.ExchangeRefreshTokenAsync(Tokens(Saas), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_client_is_never_built_for_an_environment_key_and_a_bella_chosen_server()
    {
        WriteBella(Foreign);
        Environment.SetEnvironmentVariable("BELLA_BAXTER_API_KEY", "bax-0123456789abcdef0123456789abcdef-secret");
        var provider = new BellaClientProvider(Machine(), credentials: null!, new GlobalSettings(), authService: null!);

        Assert.Throws<CredentialOriginException>(() => provider.CreateClient());
    }

    private sealed class NothingMayBeSent : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException($"A request was sent to {request.RequestUri}.");
    }
}
