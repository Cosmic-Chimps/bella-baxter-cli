using System.Net;
using System.Text;
using System.Text.Json;
using BellaBaxter.Cli.Tests.Services;
using BellaBaxter.Client;
using BellaCli.Commands;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Spectre.Console.Cli;

namespace BellaBaxter.Cli.Tests.Commands;

/// <summary>
/// Spec 108 (FR-017, SC-008) — <c>bella login</c> for a person who has no organisation yet: the login itself succeeded,
/// so it exits 0, and it says what is left to do and where. The CLI never asks for a name and never creates anything.
/// </summary>
/// <remarks>
/// Runs the real <see cref="LoginCommand"/> with a stand-in browser step (<see cref="FakeAuth"/>) and a client that answers
/// from a stub handler, so what is asserted is what the command wrote, not what a helper returned. Mutates the static
/// console, so it shares the environment collection with the other tests that do.
/// </remarks>
[Collection(ApiUrlEnvironmentCollection.Name)]
public class LoginOrganisationSetupTests : IDisposable
{
    private const string Sentence = "Finish setting up your organisation in the Bella console.";
    private const string SetupUrl = "https://console.example.test/onboarding";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TextWriter _stdout = Console.Out;
    private readonly TextWriter _stderr = Console.Error;
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    private static readonly string[] Variables = ["BELLA_BAXTER_URL", "BAXTER_URL", "BELLA_BAXTER_API_KEY", "BELLA_API_KEY"];

    private readonly string _originalCwd = Directory.GetCurrentDirectory();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bella-login-org-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _original = new();

    public LoginOrganisationSetupTests()
    {
        foreach (var v in Variables)
        {
            _original[v] = Environment.GetEnvironmentVariable(v);
            Environment.SetEnvironmentVariable(v, null);
        }

        // An empty directory: the repository's own .bella must not name the server.
        Directory.CreateDirectory(_dir);
        Directory.SetCurrentDirectory(_dir);
        Console.SetOut(_out);
        Console.SetError(_err);
    }

    public void Dispose()
    {
        Console.SetOut(_stdout);
        Console.SetError(_stderr);
        Directory.SetCurrentDirectory(_originalCwd);
        foreach (var (k, v) in _original)
            Environment.SetEnvironmentVariable(k, v);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static StoredTokens Tokens(string? orgId = null) =>
        new("access", "refresh", DateTimeOffset.UtcNow.AddMinutes(5), OrgId: orgId, OrgSlug: orgId is null ? null : "acme");

    private static string NoOrganisationBody =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = "11111111-1111-1111-1111-111111111111",
            ["email"] = "ana@example.test",
            ["hasOrganisation"] = false,
            ["suggestedOrganisationName"] = "Ana's organisation",
            ["organisationSetupUrl"] = SetupUrl,
            ["organisationSetupDetail"] = Sentence,
        });

    private static string WithOrganisationBody =>
        """{"id":"11111111-1111-1111-1111-111111111111","email":"ana@example.test","hasOrganisation":true}""";

    private sealed class Stub(HttpStatusCode status, string body, bool throws = false)
    {
        public List<string> Paths { get; } = [];

        public BellaClient Client()
        {
            var http = new HttpClient(new StubHandler(request =>
            {
                Paths.Add(request.RequestUri!.AbsolutePath);
                if (throws)
                    throw new HttpRequestException("the server is down");
                return new HttpResponseMessage(status)
                {
                    Content = new StringContent(body, Encoding.UTF8,
                        status == HttpStatusCode.OK ? "application/json" : "application/problem+json"),
                };
            }));
            var adapter = new HttpClientRequestAdapter(new AnonymousAuthenticationProvider(), httpClient: http)
            {
                BaseUrl = "https://api.example.test",
            };
            return new BellaClient(adapter);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private sealed class NoCredentials : CredentialStore
    {
        public override StoredTokens? LoadTokens() => null;
        public override StoredApiKey? LoadApiKey() => null;
    }

    private sealed class FakeAuth(ConfigService config, CredentialStore credentials, StoredTokens tokens)
        : AuthService(config, credentials, new HttpClient())
    {
        public override Task<StoredTokens> LoginWithBrowserAsync(CancellationToken ct = default) => Task.FromResult(tokens);
    }

    private sealed class EmptyRemaining : IRemainingArguments
    {
        public ILookup<string, string?> Parsed => Array.Empty<string>().ToLookup(x => x, x => (string?)null);
        public IReadOnlyList<string> Raw => [];
    }

    private sealed class CapturingOutput : IOutputWriter
    {
        public List<string> Infos { get; } = [];
        public List<string> Warnings { get; } = [];
        public List<string> Successes { get; } = [];
        public List<(string Message, string? Code)> Errors { get; } = [];
        public List<string> Documents { get; } = [];

        public Task StatusAsync(string message, Func<Task> work) => work();
        public Task StatusAsync(string message, Func<Action<string>, Task> work) => work(_ => { });
        public void WriteObject<T>(T obj) => Documents.Add(JsonSerializer.Serialize(obj));
        public void WriteList<T>(IEnumerable<T> items) { }
        public void WriteTable(string[] headers, IEnumerable<string[]> rows) { }
        public void WriteSuccess(string message) => Successes.Add(message);
        public void WriteError(string message, string? code = null) => Errors.Add((message, code));
        public void WriteWarning(string message) => Warnings.Add(message);
        public void WriteInfo(string message) => Infos.Add(message);
    }

    private static Task<int> Run(
        StoredTokens tokens, Stub stub, IOutputWriter output, bool json = false)
    {
        var config = new ConfigService(new BellaConfig("https://api.example.test"), Path.Combine(Directory.GetCurrentDirectory(), "config.json"));
        var credentials = new NoCredentials();
        var auth = new FakeAuth(config, credentials, tokens);
        var keyContext = new KeyContextService(new BellaClientProvider(config, credentials, new GlobalSettings(), auth), credentials);
        var provider = new BellaClientProvider(config, credentials, new GlobalSettings(), auth);
        var command = new LoginCommand(auth, credentials, keyContext, new ZkeService(), config, output, provider)
        {
            ClientFactory = _ => stub.Client(),
        };
        return ((ICommand)command).ExecuteAsync(
            new CommandContext([], new EmptyRemaining(), "login", null),
            new LoginSettings { Json = json },
            Ct);
    }

    [Fact]
    public async Task A_person_with_no_organisation_gets_the_sentence_and_the_link_and_exit_0()
    {
        var output = new CapturingOutput();
        var stub = new Stub(HttpStatusCode.OK, NoOrganisationBody);

        var exit = await Run(Tokens(), stub, output);

        Assert.Equal(0, exit);
        Assert.Contains("/api/v1/users/me", Assert.Single(stub.Paths));
        Assert.Contains(output.Successes, m => m.StartsWith("Logged in successfully", StringComparison.Ordinal));
        Assert.Equal($"{Sentence} {SetupUrl}", Assert.Single(output.Infos));
        Assert.Empty(output.Errors);
        Assert.Empty(output.Warnings);
        Assert.Empty(output.Documents);
    }

    [Fact]
    public async Task Json_mode_prints_one_object_with_the_flag_and_the_link_and_nothing_else_on_stdout()
    {
        // The output writer handed in is a human one: --json must win by itself (LoginSettings.Json was declared and never applied).
        var output = new CapturingOutput();
        var stub = new Stub(HttpStatusCode.OK, NoOrganisationBody);

        var exit = await Run(Tokens(), stub, output, json: true);

        Assert.Equal(0, exit);
        Assert.Empty(_err.ToString());
        using var document = JsonDocument.Parse(_out.ToString());
        var root = document.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.True(root.GetProperty("organisationSetupRequired").GetBoolean());
        Assert.Equal(SetupUrl, root.GetProperty("setupUrl").GetString());
        Assert.StartsWith("Logged in successfully", root.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(output.Successes);
        Assert.Empty(output.Infos);
    }

    [Fact]
    public async Task A_failed_organisation_check_does_not_fail_the_login()
    {
        var output = new CapturingOutput();
        var stub = new Stub(HttpStatusCode.OK, "", throws: true);

        var exit = await Run(Tokens(), stub, output);

        Assert.Equal(0, exit);
        Assert.Contains(output.Successes, m => m.StartsWith("Logged in successfully", StringComparison.Ordinal));
        Assert.Single(output.Warnings);
        Assert.DoesNotContain("the server is down", output.Warnings[0], StringComparison.Ordinal);
        Assert.Empty(output.Errors);
        Assert.Empty(output.Infos);
    }

    [Fact]
    public async Task A_server_error_on_the_check_is_a_warning_and_exit_0_in_json_too()
    {
        var output = new CapturingOutput();
        var stub = new Stub(HttpStatusCode.InternalServerError, """{"status":500}""");

        var exit = await Run(Tokens(), stub, output, json: true);

        Assert.Equal(0, exit);
        // stdout is still one document: the plain login success
        using var document = JsonDocument.Parse(_out.ToString());
        Assert.True(document.RootElement.GetProperty("success").GetBoolean());
        Assert.False(document.RootElement.TryGetProperty("organisationSetupRequired", out _));
    }

    [Fact]
    public async Task A_token_that_already_names_an_organisation_is_not_asked()
    {
        var output = new CapturingOutput();
        var stub = new Stub(HttpStatusCode.OK, WithOrganisationBody);

        var exit = await Run(Tokens(orgId: "6f1c2a52-8a0e-4d63-9a43-6f6c1e9f2b11"), stub, output);

        Assert.Equal(0, exit);
        Assert.Empty(stub.Paths);
        Assert.Empty(output.Infos);
        Assert.Contains(output.Successes, m => m.StartsWith("Logged in successfully", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_person_who_has_an_organisation_gets_no_setup_message()
    {
        var output = new CapturingOutput();
        var stub = new Stub(HttpStatusCode.OK, WithOrganisationBody);

        var exit = await Run(Tokens(), stub, output);

        Assert.Equal(0, exit);
        Assert.Single(stub.Paths);
        Assert.Empty(output.Infos);
        Assert.Empty(output.Warnings);
    }
}
