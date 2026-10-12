using System.Net;
using BellaBaxter.Client;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// <c>.bella</c> names an <c>org</c>, but a person's OAuth login carries ONE active org and the API takes its
/// tenant from the token alone. A directory for org A used from a login in org B looked up the project in
/// the wrong tenant and answered "Project 'x' not found". The CLI now refuses first and names the fix —
/// it never switches for you, because <c>.bella</c> arrives with every repository you clone.
/// </summary>
public class OrgContextRuleTests
{
    [Theory]
    [InlineData("code4fun-17d1c3", "code4fun-17d1c3")]
    [InlineData("Code4Fun-17D1C3", "code4fun-17d1c3")]
    [InlineData("  code4fun-17d1c3 ", "code4fun-17d1c3")]
    public void the_same_org_is_not_a_mismatch(string contextOrg, string activeOrg) =>
        Assert.Null(OrgContext.Check(contextOrg, activeOrg));

    [Fact]
    public void a_different_org_is_refused_naming_both_and_the_command_to_run()
    {
        var message = OrgContext.Check("code4fun-17d1c3", "jjchiw-9c45ab");

        Assert.NotNull(message);
        Assert.Contains("'code4fun-17d1c3'", message);
        Assert.Contains("'jjchiw-9c45ab'", message);
        Assert.Contains("bella org switch code4fun-17d1c3", message);
    }

    [Theory]
    [InlineData(null, "jjchiw-9c45ab")]
    [InlineData("", "jjchiw-9c45ab")]
    [InlineData("   ", "jjchiw-9c45ab")]
    [InlineData("code4fun-17d1c3", null)]
    [InlineData("code4fun-17d1c3", "")]
    [InlineData("code4fun-17d1c3", "  ")]
    [InlineData(null, null)]
    public void an_unknown_side_is_not_evidence_of_a_mismatch(string? contextOrg, string? activeOrg) =>
        Assert.Null(OrgContext.Check(contextOrg, activeOrg));
}

/// <summary>
/// The same rule driven through <see cref="ContextService"/> with a real <c>.bella</c> on disk, a stored
/// login in another org, and a client whose transport records every request.
/// </summary>
[Collection(ApiUrlEnvironmentCollection.Name)]
public class OrgContextResolutionTests : IDisposable
{
    private static readonly string[] CredentialVariables =
    [
        "BELLA_BAXTER_API_KEY",
        "BELLA_API_KEY",
        "BELLA_BAXTER_ACCESS_TOKEN",
        "BELLA_BAXTER_URL",
        "BAXTER_URL",
        "BELLA_BAXTER_PROJECT",
        "BELLA_PROJECT",
        "BELLA_BAXTER_ENV",
        "BELLA_ENV",
        "BELLA_BAXTER_TENANT",
    ];

    private readonly string _dir;
    private readonly string _originalCwd;
    private readonly Dictionary<string, string?> _originalEnv = new();

    public OrgContextResolutionTests()
    {
        _originalCwd = Directory.GetCurrentDirectory();
        foreach (var name in CredentialVariables)
        {
            _originalEnv[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }

        _dir = Path.Combine(Path.GetTempPath(), "bella-org-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        File.WriteAllText(
            Path.Combine(_dir, ".bella"),
            "org = \"code4fun-17d1c3\"\nproject = \"caravana-lacalabaza\"\nenvironment = \"local\"\n"
        );
        Directory.SetCurrentDirectory(_dir);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalCwd);
        foreach (var (name, value) in _originalEnv)
            Environment.SetEnvironmentVariable(name, value);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StoredTokens Login(string orgSlug) =>
        new("access", "refresh", DateTimeOffset.UtcNow.AddHours(1), OrgSlug: orgSlug);

    private ContextService Service(CredentialStore credentials, bool workload = false) =>
        new(new ConfigService(), new SilentOutput(), credentials) { InWorkloadEnvironment = () => workload };

    [Fact]
    public async Task a_directory_for_another_org_is_refused_before_any_project_lookup()
    {
        var client = ClientRecording(out var requests);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(new FakeCredentials(Login("jjchiw-9c45ab"))).ResolveProjectAsync(null, client, Ct));

        Assert.Contains("code4fun-17d1c3", ex.Message);
        Assert.Contains("jjchiw-9c45ab", ex.Message);
        Assert.Contains("bella org switch code4fun-17d1c3", ex.Message);
        Assert.DoesNotContain(requests, r => r.Contains("/projects"));
    }

    [Fact]
    public async Task the_strict_jwt_path_refuses_without_sending_anything()
    {
        var client = ClientRecording(out var requests);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(new FakeCredentials(Login("jjchiw-9c45ab")))
                .ResolveProjectEnvironmentAsync(null, null, client, Ct, strictJwtLocal: true));

        Assert.Contains("bella org switch code4fun-17d1c3", ex.Message);
        Assert.Empty(requests);
    }

    [Fact]
    public async Task an_explicit_project_is_the_callers_own_statement_and_is_not_refused()
    {
        var client = ClientRecording(out var requests);

        // The lookup is allowed to run (and here to find nothing) — what matters is that it is attempted.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(new FakeCredentials(Login("jjchiw-9c45ab")))
                .ResolveProjectEnvironmentAsync("other-project", "local", client, Ct, strictJwtLocal: true));

        Assert.DoesNotContain("bella org switch", ex.Message);
        Assert.Contains(requests, r => r.Contains("/projects"));
    }

    [Fact]
    public async Task the_same_org_proceeds_to_the_lookup()
    {
        var client = ClientRecording(out var requests);

        var (slug, _, _) = await Service(new FakeCredentials(Login("Code4Fun-17D1C3")))
            .ResolveProjectAsync(null, client, Ct);

        Assert.Equal("caravana-lacalabaza", slug);
        Assert.Contains(requests, r => r.Contains("/projects"));
    }

    [Fact]
    public async Task a_stored_api_key_is_pinned_to_its_own_tenant_so_the_check_is_skipped()
    {
        var key = new StoredApiKey("bax-id", "secret", "bax-id-secret");
        var client = ClientRecording(out var requests);

        await Service(new FakeCredentials(Login("jjchiw-9c45ab"), key)).ResolveProjectAsync(null, client, Ct);

        Assert.Contains(requests, r => r.Contains("/projects"));
    }

    [Fact]
    public async Task a_bearer_token_from_the_environment_skips_the_check()
    {
        // The stored login's org does not describe a credential that is not the stored login.
        Environment.SetEnvironmentVariable("BELLA_BAXTER_ACCESS_TOKEN", "from-env");
        var client = ClientRecording(out var requests);

        await Service(new FakeCredentials(Login("jjchiw-9c45ab"))).ResolveProjectAsync(null, client, Ct);

        Assert.Contains(requests, r => r.Contains("/projects"));
    }

    [Fact]
    public async Task a_workload_environment_skips_the_check()
    {
        var client = ClientRecording(out var requests);

        await Service(new FakeCredentials(Login("jjchiw-9c45ab")), workload: true).ResolveProjectAsync(null, client, Ct);

        Assert.Contains(requests, r => r.Contains("/projects"));
    }

    [Fact]
    public async Task a_session_tenant_in_the_environment_is_checked_like_the_file_org()
    {
        Environment.SetEnvironmentVariable("BELLA_BAXTER_PROJECT", "caravana-lacalabaza");
        Environment.SetEnvironmentVariable("BELLA_BAXTER_ENV", "local");
        Environment.SetEnvironmentVariable("BELLA_BAXTER_TENANT", "session-org");
        var client = ClientRecording(out _);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(new FakeCredentials(Login("jjchiw-9c45ab"))).ResolveProjectAsync(null, client, Ct));

        Assert.Contains("bella org switch session-org", ex.Message);
    }

    // ── plumbing ──────────────────────────────────────────────────────────────

    private static BellaClient ClientRecording(out List<string> requests)
    {
        var seen = new List<string>();
        requests = seen;
        var http = new HttpClient(new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            seen.Add(path);
            // /keys/me has no project for a person's login; a project lookup answers with a project.
            return path.Contains("/projects")
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"slug":"caravana-lacalabaza","name":"Caravana","id":"p1"}""",
                        System.Text.Encoding.UTF8,
                        "application/json"),
                }
                : new HttpResponseMessage(HttpStatusCode.Forbidden);
        }));
        var adapter = new HttpClientRequestAdapter(new AnonymousAuthenticationProvider(), httpClient: http)
        {
            BaseUrl = "https://api.example.com",
        };
        return new BellaClient(adapter);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    /// <summary>A credential store that holds exactly what the test gives it — never the developer's real login.</summary>
    private sealed class FakeCredentials(StoredTokens? tokens, StoredApiKey? apiKey = null) : CredentialStore
    {
        public override StoredTokens? LoadTokens() => tokens;
        public override StoredApiKey? LoadApiKey() => apiKey;
    }

    private sealed class SilentOutput : IOutputWriter
    {
        public Task StatusAsync(string message, Func<Task> work) => work();
        public Task StatusAsync(string message, Func<Action<string>, Task> work) => work(_ => { });
        public void WriteObject<T>(T obj) { }
        public void WriteList<T>(IEnumerable<T> items) { }
        public void WriteTable(string[] headers, IEnumerable<string[]> rows) { }
        public void WriteSuccess(string message) { }
        public void WriteError(string message, string? code = null) { }
        public void WriteWarning(string message) { }
        public void WriteInfo(string message) { }
    }
}
