using System.Net;
using System.Text;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// Spec 108 (FR-018) — a stored-person client turns the API's 403 <c>organisation-setup-required</c> into
/// <see cref="OrganisationSetupRequiredException"/>, and nothing else. Any other 403 reaches the command unchanged, body
/// intact, because a person's 403 usually means something specific that the command knows how to read.
/// </summary>
public class OrganisationSetupRefusalTests
{
    private const string Sentence = "Finish setting up your organisation in the Bella console.";
    private const string SetupUrl = "https://console.example.test/onboarding";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ValidLogin : CredentialStore
    {
        public override StoredTokens? LoadTokens() =>
            new("access", "refresh", DateTimeOffset.UtcNow.AddMinutes(30));
    }

    private sealed class Inner(HttpStatusCode status, string body, string contentType = "application/problem+json") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType),
            });
    }

    private static HttpMessageInvoker Pipeline(Inner inner)
    {
        var dir = Path.Combine(Path.GetTempPath(), "bella-org-refusal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var config = new ConfigService(new BellaConfig("https://api.example.test"), Path.Combine(dir, "config.json"));
        var credentials = new ValidLogin();
        var auth = new AuthService(config, credentials, new HttpClient());
        return new HttpMessageInvoker(new TokenRefreshHandler(auth) { InnerHandler = inner });
    }

    private static string Refusal(string type = "organisation-setup-required", bool withUrl = true) =>
        withUrl
            ? $$"""{"type":"{{type}}","title":"The organisation is not set up.","status":403,"detail":"{{Sentence}}","setupUrl":"{{SetupUrl}}"}"""
            : $$"""{"type":"{{type}}","title":"The organisation is not set up.","status":403,"detail":"{{Sentence}}"}""";

    [Fact]
    public async Task The_refusal_becomes_an_exception_carrying_the_sentence_and_the_link()
    {
        var invoker = Pipeline(new Inner(HttpStatusCode.Forbidden, Refusal()));

        var thrown = await Assert.ThrowsAsync<OrganisationSetupRequiredException>(() =>
            invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/api/v1/projects"), Ct));

        Assert.Equal(Sentence, thrown.Detail);
        Assert.Equal(SetupUrl, thrown.SetupUrl);
        Assert.Equal($"{Sentence} {SetupUrl}", thrown.Message);
        Assert.IsAssignableFrom<InvalidOperationException>(thrown); // the ~110 catch sites print ex.Message
    }

    [Fact]
    public async Task A_type_given_as_a_uri_ending_in_the_code_is_recognised_too()
    {
        var invoker = Pipeline(new Inner(HttpStatusCode.Forbidden, Refusal("https://bella.example/problems/organisation-setup-required")));

        await Assert.ThrowsAsync<OrganisationSetupRequiredException>(() =>
            invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/x"), Ct));
    }

    [Fact]
    public async Task A_refusal_with_no_link_still_carries_the_sentence()
    {
        var invoker = Pipeline(new Inner(HttpStatusCode.Forbidden, Refusal(withUrl: false)));

        var thrown = await Assert.ThrowsAsync<OrganisationSetupRequiredException>(() =>
            invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/x"), Ct));

        Assert.Equal(Sentence, thrown.Message);
        Assert.Null(thrown.SetupUrl);
    }

    [Theory]
    [InlineData("person-only")]
    [InlineData("audit-streaming-plan-required")]
    [InlineData("not-organisation-setup-required-at-all")]
    public async Task Any_other_403_reaches_the_command_unchanged_with_its_body(string type)
    {
        var body = Refusal(type);
        var invoker = Pipeline(new Inner(HttpStatusCode.Forbidden, body));

        var response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/x"), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(body, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_403_with_a_body_that_is_not_json_passes_through()
    {
        var invoker = Pipeline(new Inner(HttpStatusCode.Forbidden, "forbidden", "text/plain"));

        var response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/x"), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("forbidden", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_success_is_untouched()
    {
        var invoker = Pipeline(new Inner(HttpStatusCode.OK, "{}", "application/json"));

        var response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/x"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void An_exception_that_escapes_a_command_is_printed_on_stderr_with_exit_1()
    {
        var stdout = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(stdout) });
        var stderr = new StringWriter();

        var exit = CliExceptionHandler.Handle(
            new OrganisationSetupRequiredException(Sentence, SetupUrl), console, stderr, cancellationExitCode: 130, arguments: []);

        Assert.Equal(1, exit);
        Assert.Equal($"{Sentence} {SetupUrl}", stderr.ToString().TrimEnd());
        Assert.Equal(string.Empty, stdout.ToString());
    }

    [Fact]
    public void The_refusal_is_found_when_something_wraps_it()
    {
        var stdout = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(stdout) });
        var stderr = new StringWriter();

        var exit = CliExceptionHandler.Handle(
            new AggregateException(new OrganisationSetupRequiredException(Sentence, SetupUrl)),
            console, stderr, cancellationExitCode: 130, arguments: []);

        Assert.Equal(1, exit);
        Assert.Contains(SetupUrl, stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, stdout.ToString());
    }
}
