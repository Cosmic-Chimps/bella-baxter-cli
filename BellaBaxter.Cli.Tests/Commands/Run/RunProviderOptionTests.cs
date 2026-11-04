using System.Net;
using System.Text;
using BellaBaxter.Client;
using BellaBaxter.Client.Models;
using BellaCli.Commands.Run;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;

namespace BellaBaxter.Cli.Tests.Commands.Run;

/// <summary>
/// Issue #1616 — <c>bella run --provider &lt;slug&gt;</c> is honoured: the child receives that provider's secrets,
/// on top of the project's globals, and nothing from the environment's other providers.
/// </summary>
/// <remarks>
/// <para>The option was declared and never read: <c>RunCommand</c> always called the bulk environment endpoint, which
/// merges every provider (last one wins), so <c>--provider A</c> handed the child B's secrets too. The reads here go
/// through the real generated <see cref="BellaClient"/> over a stub that answers each route the way the API does and
/// RECORDS which routes were asked — the point is as much what is not requested as what is.</para>
/// </remarks>
public class RunProviderOptionTests
{
    private const string Origin = "https://api.example.test";

    private const string Globals =
        """{"projectRef":"p","projectSlug":"p","globalSecretProviderId":"00000000-0000-0000-0000-000000000001","secrets":["""
        + """{"key":"LOG_LEVEL","value":"info","description":null,"createdAt":null,"updatedAt":null,"type":"string","ignoreInScan":false,"tags":[]},"""
        + """{"key":"APP_NAME","value":"acme","description":null,"createdAt":null,"updatedAt":null,"type":"string","ignoreInScan":false,"tags":[]},"""
        + """{"key":"UNSET","value":null,"description":null,"createdAt":null,"updatedAt":null,"type":"string","ignoreInScan":false,"tags":[]}]}""";

    private const string ProviderA =
        """[{"key":"LOG_LEVEL","value":"debug","description":null,"createdAt":null,"updatedAt":null,"type":"string","ignoreInScan":false},"""
        + """{"key":"DATABASE_URL","value":"postgres://a/app","description":null,"createdAt":null,"updatedAt":null,"type":"string","ignoreInScan":false}]""";

    private const string ProviderB =
        """[{"key":"DATABASE_URL","value":"postgres://b/app","description":null,"createdAt":null,"updatedAt":null,"type":"string","ignoreInScan":false},"""
        + """{"key":"B_ONLY","value":"1","description":null,"createdAt":null,"updatedAt":null,"type":"string","ignoreInScan":false}]""";

    // What the bulk route would merge: every provider, last one wins.
    private const string Bulk =
        """{"secrets":{"LOG_LEVEL":"debug","APP_NAME":"acme","DATABASE_URL":"postgres://b/app","B_ONLY":"1"},"version":7}""";

    private const string Providers =
        """[{"id":"00000000-0000-0000-0000-00000000000a","providerId":"00000000-0000-0000-0000-00000000000a","providerSlug":"a","providerName":"A","providerType":"OpenBao","providerMetaType":"Secrets"},"""
        + """{"id":"00000000-0000-0000-0000-00000000000b","providerId":"00000000-0000-0000-0000-00000000000b","providerSlug":"b","providerName":"B","providerType":"AwsSecretsManager","providerMetaType":"Secrets"},"""
        + """{"id":"00000000-0000-0000-0000-00000000000c","providerId":"00000000-0000-0000-0000-00000000000c","providerSlug":"certs","providerName":"Certs","providerType":"Gigamon","providerMetaType":"CertificateRotation"}]""";

    /// <summary>Answers the four routes `run` can touch and records which it did.</summary>
    private sealed class Server : HttpMessageHandler
    {
        public List<string> Paths { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            // A provider route that answers with no body at all, as a 200 and as a 204.
            if (path == "/api/v1/projects/p/environments/e/providers/empty-200/secrets")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) });
            if (path == "/api/v1/projects/p/environments/e/providers/empty-204/secrets")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            var body = path switch
            {
                "/api/v1/projects/p/secrets" => Globals,
                "/api/v1/projects/p/environments/e/secrets" => Bulk,
                "/api/v1/projects/p/environments/e/providers/a/secrets" => ProviderA,
                "/api/v1/projects/p/environments/e/providers/b/secrets" => ProviderB,
                "/api/v1/projects/p/environments/e/providers" => Providers,
                _ => null,
            };
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static (BellaClient Client, Server Server) ClientAndServer()
    {
        var server = new Server();
        var client = new BellaClient(new HttpClientRequestAdapter(
            new AnonymousAuthenticationProvider(), httpClient: new HttpClient(server)) { BaseUrl = Origin });
        return (client, server);
    }

    [Fact]
    public async Task With_a_provider_the_child_gets_that_providers_secrets_and_not_the_others()
    {
        var (client, server) = ClientAndServer();

        var (secrets, _) = await RunCommand.FetchSecretsAsync(client, "p", "e", "a", CancellationToken.None);

        Assert.Equal("postgres://a/app", secrets["DATABASE_URL"]);
        Assert.False(secrets.ContainsKey("B_ONLY"), "a secret that only provider B holds reached the child");
        // The other provider and the bulk route were never asked — not filtered client-side, never read.
        Assert.DoesNotContain("/providers/b/secrets", string.Join("\n", server.Paths));
        Assert.DoesNotContain("/api/v1/projects/p/environments/e/secrets", server.Paths);
    }

    [Fact]
    public async Task Project_globals_reach_the_child_and_the_provider_wins_key_by_key()
    {
        var (client, _) = ClientAndServer();

        var (secrets, _) = await RunCommand.FetchSecretsAsync(client, "p", "e", "a", CancellationToken.None);

        Assert.Equal("acme", secrets["APP_NAME"]);        // a global the provider does not define
        Assert.Equal("debug", secrets["LOG_LEVEL"]);      // defined by both: the provider's value
        Assert.Equal("", secrets["UNSET"]);               // a global with no value is an empty string, as the bulk route has it
        Assert.Equal(4, secrets.Count);
    }

    [Fact]
    public async Task Without_a_provider_the_bulk_route_is_read_exactly_as_before()
    {
        var (client, server) = ClientAndServer();

        var (secrets, version) = await RunCommand.FetchSecretsAsync(client, "p", "e", null, CancellationToken.None);

        Assert.Equal(["/api/v1/projects/p/environments/e/secrets"], server.Paths);
        Assert.Equal("postgres://b/app", secrets["DATABASE_URL"]); // last provider wins there; not this issue's to change
        Assert.Equal(7, version);
    }

    [Fact]
    public async Task A_provider_that_is_not_attached_or_does_not_store_secrets_is_refused_with_the_write_rules_text()
    {
        // The same rule and the same text as `secrets set --provider` (spec 020, SecretProviderSelection), applied
        // BEFORE any secret is read — RunCommand returns 1 on a null here and never reaches the fetch or the child.
        var (client, server) = ClientAndServer();
        var output = new CapturingOutput();
        var resolver = new SecretProviderResolver(output);

        var unattached = await resolver.ResolveAsync(client, "p", "e", "somewhere-else", CancellationToken.None);
        var certsOnly = await resolver.ResolveAsync(client, "p", "e", "certs", CancellationToken.None);
        var canonical = await resolver.ResolveAsync(client, "p", "e", "A", CancellationToken.None);

        Assert.Null(unattached);
        Assert.Null(certsOnly);
        Assert.Equal("a", canonical); // matched without case, read with the slug the server knows
        Assert.Contains("not attached", output.Errors[0].Message);
        Assert.Contains("does not store", output.Errors[1].Message);
        Assert.All(server.Paths, p => Assert.EndsWith("/providers", p)); // only the providers list was read
    }

    [Theory]
    [InlineData("empty-200")]
    [InlineData("empty-204")]
    public async Task A_provider_read_with_no_body_fails_instead_of_running_the_child_on_the_globals_alone(string provider)
    {
        // Security's review: null from the provider read means "could not read". Falling back to an empty map would
        // start the child with the globals only and no warning. FetchSecretsAsync throws; RunCommand turns that into
        // "Failed to fetch secrets: …" and exit 1 before the child starts.
        var (client, _) = ClientAndServer();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunCommand.FetchSecretsAsync(client, "p", "e", provider, CancellationToken.None));

        Assert.Contains($"provider '{provider}'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Merge_keeps_every_global_the_provider_does_not_override_and_compares_keys_exactly()
    {
        var globals = new[]
        {
            new GlobalSecretItem { Key = "db", Value = "global-lower" },
            new GlobalSecretItem { Key = "DB", Value = "global-upper" },
            new GlobalSecretItem { Key = null, Value = "ignored" },
        };
        var provider = new Dictionary<string, string> { ["DB"] = "provider" };

        var merged = RunSecretSources.Merge(globals, provider);

        Assert.Equal("global-lower", merged["db"]); // the bulk route's dictionary is ordinal too
        Assert.Equal("provider", merged["DB"]);
        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void The_help_text_states_the_watch_limitation()
    {
        // With --provider, --watch still polls the ENVIRONMENT's version, so a change in any provider restarts the
        // process. That is a documented limitation of this delivery, and --help is where the operator meets it.
        var description = typeof(RunCommand.Settings)
            .GetProperty(nameof(RunCommand.Settings.Provider))!
            .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
            .Cast<System.ComponentModel.DescriptionAttribute>()
            .Single()
            .Description;

        Assert.Contains("--watch", description);
        Assert.Contains("any provider", description);
        Assert.Contains("global", description);
    }

    private sealed class CapturingOutput : IOutputWriter
    {
        public List<(string Message, string? Code)> Errors { get; } = new();
        public Task StatusAsync(string message, Func<Task> work) => work();
        public Task StatusAsync(string message, Func<Action<string>, Task> work) => work(_ => { });
        public void WriteObject<T>(T obj) { }
        public void WriteList<T>(IEnumerable<T> items) { }
        public void WriteTable(string[] headers, IEnumerable<string[]> rows) { }
        public void WriteSuccess(string message) { }
        public void WriteError(string message, string? code = null) => Errors.Add((message, code));
        public void WriteWarning(string message) { }
        public void WriteInfo(string message) { }
    }
}
