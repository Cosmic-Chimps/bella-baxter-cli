using System.Net;
using System.Text;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// #1610 — the pieces behind "Your session expired. Run `bella login`.": the refresh the server REFUSES becomes
/// <see cref="SessionExpiredException"/> (and an outage does not), both output writers reduce any line carrying it to the
/// sentence, and the exception handler prints it for a command that does not catch. <c>SessionExpiredExitContractTests</c>
/// runs the real <c>bella</c> for the whole path.
/// </summary>
[Collection(ApiUrlEnvironmentCollection.Name)]
public class SessionExpiredHandlingTests : IDisposable
{
    private const string Sentence = "Your session expired. Run `bella login`.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The refresh reads the server address, which walks up from the working directory for a .bella: run in an empty one, and
    // without the address variables, so the repository's own .bella and the developer's environment never decide a test.
    private static readonly string[] Variables = ["BELLA_BAXTER_URL", "BAXTER_URL"];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bella-expired-" + Guid.NewGuid().ToString("N"));
    private readonly string _originalCwd = Directory.GetCurrentDirectory();
    private readonly Dictionary<string, string?> _original = [];

    public SessionExpiredHandlingTests()
    {
        foreach (var v in Variables)
        {
            _original[v] = Environment.GetEnvironmentVariable(v);
            Environment.SetEnvironmentVariable(v, null);
        }
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

    /// <summary>Serves the auth config and answers the token endpoint with <paramref name="status"/>.</summary>
    private sealed class Keycloak(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v1/auth/config")
                return Task.FromResult(Json(HttpStatusCode.OK,
                    """{"keycloakUrl":"https://auth.example.test","realm":"bella-baxter","cliClientId":"bella-cli"}"""));
            if (path.EndsWith("/protocol/openid-connect/token", StringComparison.Ordinal))
                return Task.FromResult(Json(status, body));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(HttpStatusCode code, string text) =>
            new(code) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }

    private static AuthService Auth(HttpStatusCode status, string body) =>
        new(new ConfigService(new BellaConfig("https://api.example.test")), credentials: null!, new HttpClient(new Keycloak(status, body)));

    private static StoredTokens Lapsed() =>
        new("access", "refresh", DateTimeOffset.UtcNow.AddHours(-2), Origin: "https://api.example.test");

    private const string Sentinel = "SENTINEL-body-text-never-shown";
    private const string Host = "auth.example.test"; // the token endpoint the fake serves (its host is all a message may name)

    [Theory]
    [InlineData("""{"error":"invalid_grant","error_description":"Token is not active SENTINEL-body-text-never-shown"}""")]
    [InlineData("""{"error":"invalid_grant","error_description":"Session not active"}""")]
    [InlineData("""{"error":"invalid_grant","error_description":"Session doesn't have required client"}""")]
    public async Task A_400_invalid_grant_is_a_session_expiry_with_the_plain_sentence(string body)
    {
        var thrown = await Assert.ThrowsAsync<SessionExpiredException>(() =>
            Auth(HttpStatusCode.BadRequest, body).ExchangeRefreshTokenAsync(Lapsed(), Ct));

        Assert.Equal(Sentence, thrown.Message);
        Assert.Equal(SessionEndKind.Expired, thrown.Kind);
        Assert.DoesNotContain("invalid_grant", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, thrown.ToString(), StringComparison.Ordinal);
        Assert.IsAssignableFrom<InvalidOperationException>(thrown); // the ~110 catch sites print ex.Message
    }

    // A 400 that is not invalid_grant, or has no OAuth error to read at all, says nothing about the session ending: it is
    // "refused" (#2002 decision: invalid_request / invalid_client / an HTML page from a proxy are not an expiry).
    [Theory]
    [InlineData("""{"error":"invalid_request","error_description":"SENTINEL-body-text-never-shown"}""")]
    [InlineData("""{"error":"invalid_client"}""")]
    [InlineData("""{"error":42}""")]
    [InlineData("<html>SENTINEL-body-text-never-shown</html>")]
    [InlineData("")]
    public async Task A_400_that_is_not_invalid_grant_is_refused_not_expired(string body)
    {
        var thrown = await Assert.ThrowsAsync<SessionExpiredException>(() =>
            Auth(HttpStatusCode.BadRequest, body).ExchangeRefreshTokenAsync(Lapsed(), Ct));

        Assert.Equal(SessionEndKind.ServerRefused, thrown.Kind);
        Assert.Equal(SessionExpiredException.ServerRefusedSentence(400, Host), thrown.Message);
        Assert.NotEqual(Sentence, thrown.Message);
        Assert.DoesNotContain(Sentinel, thrown.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"error":"unauthorized_client","error_description":"SENTINEL-body-text-never-shown"}""")]
    [InlineData("""{"error":"invalid_client"}""")]
    [InlineData("")]
    public async Task A_401_means_the_clients_misconfigured_and_is_not_an_expiry(string body)
    {
        var thrown = await Assert.ThrowsAsync<SessionExpiredException>(() =>
            Auth(HttpStatusCode.Unauthorized, body).ExchangeRefreshTokenAsync(Lapsed(), Ct));

        Assert.Equal(SessionEndKind.ClientRejected, thrown.Kind);
        Assert.Equal(
            "The sign-in server rejected this CLI's client (HTTP 401 from auth.example.test). Run `bella login`; if it repeats, report it.",
            thrown.Message);
        Assert.DoesNotContain(Sentinel, thrown.ToString(), StringComparison.Ordinal);
    }

    // Keycloak never answers 403 here: it is a WAF, bot protection or a proxy in front of it. Nor 404 or 418.
    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    [InlineData((HttpStatusCode)418)]
    public async Task Any_other_4xx_is_refused_and_names_the_host_but_never_the_body_or_the_path(HttpStatusCode status)
    {
        var thrown = await Assert.ThrowsAsync<SessionExpiredException>(() =>
            Auth(status, """{"error":"invalid_grant","error_description":"SENTINEL-body-text-never-shown"}""")
                .ExchangeRefreshTokenAsync(Lapsed(), Ct));

        Assert.Equal(SessionEndKind.ServerRefused, thrown.Kind);
        Assert.Equal(
            $"The sign-in server refused the refresh (HTTP {(int)status} from auth.example.test). Try again; if it repeats, run `bella login` and report it.",
            thrown.Message);
        Assert.DoesNotContain("expired", thrown.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/realms", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, thrown.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("invalid_grant", thrown.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Failed to list projects: The sign-in server refused the refresh (HTTP 403 from auth.example.test). Try again; if it repeats, run `bella login` and report it.",
        "The sign-in server refused the refresh (HTTP 403 from auth.example.test). Try again; if it repeats, run `bella login` and report it.")]
    [InlineData("Token refresh failed: The sign-in server rejected this CLI's client (HTTP 401 from auth.example.test). Run `bella login`; if it repeats, report it.",
        "The sign-in server rejected this CLI's client (HTTP 401 from auth.example.test). Run `bella login`; if it repeats, report it.")]
    public void A_line_that_carries_a_refusal_sentence_is_reduced_to_it(string line, string expected) =>
        Assert.Equal(expected, SessionExpiredException.Normalize(line));

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task An_outage_or_a_throttle_says_nothing_about_the_session_and_is_not_called_one(HttpStatusCode status)
    {
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Auth(status, "try later").ExchangeRefreshTokenAsync(Lapsed(), Ct));
    }

    [Fact]
    public async Task The_refusal_passes_through_the_handler_unchanged_and_nothing_is_sent()
    {
        var invoker = new HttpMessageInvoker(new TokenRefreshHandler(new Refusing(SessionExpiredException.Refused())) { InnerHandler = new Throwing() });

        var thrown = await Assert.ThrowsAsync<SessionExpiredException>(() =>
            invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/api/v1/projects"), Ct));

        Assert.Equal(Sentence, thrown.Message);
    }

    [Fact]
    public async Task An_outage_during_the_refresh_is_not_turned_into_an_expiry_by_the_handler()
    {
        var invoker = new HttpMessageInvoker(new TokenRefreshHandler(new Refusing(new HttpRequestException("connection refused"))) { InnerHandler = new Throwing() });

        var thrown = await Assert.ThrowsAsync<HttpRequestException>(() =>
            invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/api/v1/projects"), Ct));

        Assert.DoesNotContain("session", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An <see cref="AuthService"/> whose refresh fails with a given exception, over a stored login that has lapsed.</summary>
    private sealed class Refusing(Exception failure) : AuthService(
        new ConfigService(new BellaConfig("https://api.example.test")), new LapsedLogin(), new HttpClient())
    {
        public override Task<StoredTokens> RefreshAsync(CancellationToken ct = default) => Task.FromException<StoredTokens>(failure);
    }

    private sealed class LapsedLogin : CredentialStore
    {
        public override StoredTokens? LoadTokens() => Lapsed();
    }

    private sealed class Throwing : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the request must not be sent with a session that could not be renewed");
    }

    // ── the sentence, wherever a command wrapped it ──────────────────────────────────────────

    [Theory]
    [InlineData("Failed to list projects: " + Sentence)]
    [InlineData("Token refresh failed: " + Sentence)]
    [InlineData("Failed to refresh JWT token: " + Sentence + "\nRun: bella login")]
    [InlineData(Sentence)]
    public void A_line_that_carries_the_sentence_is_reduced_to_it(string message) =>
        Assert.Equal(Sentence, SessionExpiredException.Normalize(message));

    [Theory]
    [InlineData("Not logged in. Run 'bella login' first.")]
    [InlineData("Failed to list projects: connection refused")]
    [InlineData("")]
    public void Any_other_message_is_left_alone(string message) =>
        Assert.Equal(message, SessionExpiredException.Normalize(message));

    [Fact]
    public void The_human_writer_prints_the_sentence_alone_on_stderr()
    {
        var stderr = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(stderr) });
        console.Profile.Width = 4096;

        new HumanOutputWriter(console).WriteError("Failed to get secrets: " + Sentence);

        var written = stderr.ToString();
        Assert.Contains(Sentence, written, StringComparison.Ordinal);
        Assert.DoesNotContain("Failed to get secrets", written, StringComparison.Ordinal);
    }

    [Fact]
    public void The_json_writer_prints_the_sentence_alone_on_stderr()
    {
        var originalError = Console.Error;
        var stderr = new StringWriter();
        try
        {
            Console.SetError(stderr);
            new JsonOutputWriter().WriteError("Failed to get secrets: " + Sentence, "failed");
        }
        finally
        {
            Console.SetError(originalError);
        }

        using var doc = System.Text.Json.JsonDocument.Parse(stderr.ToString());
        Assert.Equal(Sentence, doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void A_session_expiry_that_escapes_a_command_is_printed_on_stderr_with_exit_1_whatever_wraps_it()
    {
        foreach (var escaped in new Exception[]
        {
            SessionExpiredException.Refused(),
            new AggregateException(SessionExpiredException.Refused()),
            new InvalidOperationException("wrapper", SessionExpiredException.Refused(new HttpRequestException("400 invalid_grant"))),
        })
        {
            var stdout = new StringWriter();
            var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(stdout) });
            var stderr = new StringWriter();

            var exit = CliExceptionHandler.Handle(escaped, console, stderr, cancellationExitCode: 130, arguments: []);

            Assert.Equal(1, exit);
            Assert.Equal(Sentence, stderr.ToString().TrimEnd());
            Assert.Equal(string.Empty, stdout.ToString());
        }
    }

    [Fact]
    public void A_refusal_that_is_not_an_expiry_escaping_a_command_prints_its_own_sentence_on_stderr_with_exit_1()
    {
        foreach (var (escaped, expected) in new (Exception, string)[]
        {
            (SessionExpiredException.ServerRefused(403, Host), SessionExpiredException.ServerRefusedSentence(403, Host)),
            (new AggregateException(SessionExpiredException.ClientRejected(Host)), SessionExpiredException.ClientRejectedSentence(Host)),
        })
        {
            var stdout = new StringWriter();
            var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(stdout) });
            var stderr = new StringWriter();

            var exit = CliExceptionHandler.Handle(escaped, console, stderr, cancellationExitCode: 130, arguments: []);

            Assert.Equal(1, exit);
            Assert.Equal(expected, stderr.ToString().TrimEnd());
            Assert.Equal(string.Empty, stdout.ToString());
        }
    }

    [Fact]
    public void The_exception_chain_is_searched_for_the_expiry()
    {
        Assert.Null(SessionExpiredException.Find(new InvalidOperationException("plain")));
        Assert.NotNull(SessionExpiredException.Find(new Exception("outer", SessionExpiredException.Refused())));
    }
}
