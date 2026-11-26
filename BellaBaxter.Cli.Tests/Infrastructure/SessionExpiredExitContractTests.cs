using BellaBaxter.Cli.Tests.Helpers;
using BellaBaxter.Cli.Tests.Services;
using BellaCli.Services;

namespace BellaBaxter.Cli.Tests.Infrastructure;

/// <summary>
/// #1610 — a login that ran out says so in ONE plain sentence on every command path where the server refuses the refresh,
/// on STDERR with exit 1: never a token-endpoint error, a status code or a stack trace. Runs the REAL built <c>bella</c> as a
/// child process against a loopback server that serves the auth config and answers the token endpoint 400 <c>invalid_grant</c>
/// the way Keycloak does for the ways a session ends (idle, maximum lifespan, revoked, an offline token at its cap), with a
/// stored login whose access token has already lapsed so the first thing the command does is refresh. The in-process tests
/// prove the pieces (<c>SessionExpiredHandlingTests</c>); only this proves the process.
/// </summary>
[Collection(ApiUrlEnvironmentCollection.Name)]
public class SessionExpiredExitContractTests
{
    private const string Realm = "bella-baxter";
    private const string TokenPath = "/realms/" + Realm + "/protocol/openid-connect/token";

    private static string Body(string error, string description) =>
        $$"""{"error":"{{error}}","error_description":"{{description}}"}""";

    // What Keycloak 26 answers a refresh with once the session is over. The words differ, the status does not.
    public static TheoryData<string, string> EndedSessions() => new()
    {
        { "invalid_grant", "Token is not active" },                  // the SSO session idle limit
        { "invalid_grant", "Session not active" },                   // the maximum lifespan
        { "invalid_grant", "Session doesn't have required client" }, // revoked: signed out, or an admin ended it
        { "invalid_grant", "Offline user session not found" },       // an older CLI's offline token that reached its cap
    };

    private static FakeBellaServer Server(int status, string body)
    {
        var server = new FakeBellaServer();
        server.On("GET", "/api/v1/auth/config", $$"""{"keycloakUrl":"{{server.Url}}","realm":"{{Realm}}","cliClientId":"bella-cli"}""");
        server.OnStatus("POST", TokenPath, status, body);
        return server;
    }

    private static async Task<(int Exit, string Stdout, string Stderr, string Said)> Run(
        FakeBellaServer server, BellaProcess bella, params string[] args)
    {
        StoredLoginSeed.Write(bella.Home, server.Url, DateTimeOffset.UtcNow.AddHours(-2));
        var (exit, stdout, stderr) = await bella.RunAsync(
            new Dictionary<string, string> { ["BELLA_BAXTER_URL"] = server.Url },
            args);
        return (exit, stdout, stderr, BellaProcess.Said(string.Empty, stderr));
    }

    private static void AssertOnlyTheSentence(int exit, string stdout, string stderr, string said, FakeBellaServer server)
    {
        Assert.True(server.CountOf("POST", TokenPath) >= 1, "the command never tried to refresh, so it did not meet the refusal: " + stderr);
        Assert.Equal(1, exit);
        Assert.Contains(SessionExpiredException.Sentence, said, StringComparison.Ordinal);
        Assert.DoesNotContain(SessionExpiredException.Sentence, stdout, StringComparison.Ordinal);
        foreach (var leaked in new[]
        {
            "invalid_grant", "Token is not active", "Session not active", "Session doesn't have", "Offline user session",
            "Bad Request", "StatusCode", "status code", "HttpRequestException", "Exception", "   at ",
            "Session expired. Run", "Failed to ", "Could not ", "Token refresh failed",
        })
        {
            Assert.DoesNotContain(leaked, said, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(EndedSessions))]
    public async Task A_command_whose_refresh_is_refused_prints_the_sentence_whatever_keycloak_said(string error, string description)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The child's credential directory rules are Unix-only here.");

        using var server = Server(400, Body(error, description));
        using var bella = new BellaProcess();

        var (exit, stdout, stderr, said) = await Run(server, bella, "projects", "list");

        AssertOnlyTheSentence(exit, stdout, stderr, said, server);
    }

    private const string Sentinel = "SENTINEL-body-text-never-shown";

    // #2002 — only 400 invalid_grant is an expiry. A 401 is the CLI's client being misconfigured, and any other 4xx (a 403
    // from a WAF or a proxy in front of the sign-in server, above all) is a refusal that must not be called an expiry.
    private static async Task AssertRefusalSentence(int status, string body, string expected)
    {
        using var server = Server(status, body);
        using var bella = new BellaProcess();

        var (exit, stdout, stderr, said) = await Run(server, bella, "projects", "list");

        Assert.True(server.CountOf("POST", TokenPath) >= 1, "the command never tried to refresh: " + stderr);
        Assert.Equal(1, exit);
        Assert.Contains(expected, said, StringComparison.Ordinal);
        Assert.DoesNotContain(expected, stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(SessionExpiredException.Sentence, said, StringComparison.Ordinal);
        foreach (var leaked in new[] { Sentinel, "invalid_grant", "unauthorized_client", "StatusCode", "Exception", "   at ", "/realms", "Failed to ", "Token refresh failed" })
            Assert.DoesNotContain(leaked, said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_401_from_the_token_endpoint_is_a_client_misconfiguration_not_an_expiry()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The child's credential directory rules are Unix-only here.");
        await AssertRefusalSentence(401, Body("unauthorized_client", Sentinel),
            SessionExpiredException.ClientRejectedSentence("127.0.0.1"));
    }

    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(418)]
    public async Task Any_other_4xx_from_the_token_endpoint_is_a_refusal_naming_the_host_not_an_expiry(int status)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The child's credential directory rules are Unix-only here.");
        await AssertRefusalSentence(status, "<html>" + Sentinel + "</html>",
            SessionExpiredException.ServerRefusedSentence(status, "127.0.0.1"));
    }

    [Fact]
    public async Task A_400_that_is_not_invalid_grant_is_a_refusal_not_an_expiry()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The child's credential directory rules are Unix-only here.");
        await AssertRefusalSentence(400, Body("invalid_request", Sentinel),
            SessionExpiredException.ServerRefusedSentence(400, "127.0.0.1"));
    }

    [Fact]
    public async Task A_403_refusal_stops_a_child_runner_before_it_starts_the_child()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The child's credential directory rules are Unix-only here.");

        using var server = Server(403, Sentinel);
        using var bella = new BellaProcess();

        var (exit, _, stderr, said) = await Run(
            server, bella, ["exec", "-p", "sandbox", "-e", "dev", "--", .. bella.ChildCommand]);

        Assert.Equal(1, exit);
        Assert.Contains(SessionExpiredException.ServerRefusedSentence(403, "127.0.0.1"), said, StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, stderr, StringComparison.Ordinal);
        Assert.False(bella.ChildStarted);
    }

    // Every route to the refresh a stored login takes, by the way the command handles what comes back.
    public static TheoryData<string> CommandPaths() => new()
    {
        "secrets list -p sandbox -e dev",    // a command with its own catch-all around the call ("Failed to ...: <message>")
        "whoami",                            // classifies the failure (ServerProbe)
        "auth status",                       // EnsureValidTokenAsync, the status document on stdout
        "auth refresh",                      // RefreshAsync, "Token refresh failed: <message>"
        "org list",                          // a command that may not catch at all: Spectre's handler, CliExceptionHandler
    };

    [Theory]
    [MemberData(nameof(CommandPaths))]
    public async Task Every_command_path_prints_the_same_sentence(string command)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The child's credential directory rules are Unix-only here.");

        using var server = Server(400, Body("invalid_grant", "Token is not active"));
        using var bella = new BellaProcess();

        var (exit, stdout, stderr, said) = await Run(server, bella, command.Split(' '));

        AssertOnlyTheSentence(exit, stdout, stderr, said, server);
    }

    [Theory]
    [InlineData("exec")]
    [InlineData("sdk run")]
    public async Task A_child_runner_stops_before_it_starts_the_child(string command)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The child's credential directory rules are Unix-only here.");

        using var server = Server(400, Body("invalid_grant", "Session not active"));
        using var bella = new BellaProcess();

        var (exit, stdout, stderr, said) = await Run(
            server, bella, [.. command.Split(' '), "-p", "sandbox", "-e", "dev", "--", .. bella.ChildCommand]);

        AssertOnlyTheSentence(exit, stdout, stderr, said, server);
        Assert.False(bella.ChildStarted, $"`bella {command}` started the child with a session that could not be renewed.");
    }

    [Fact]
    public async Task A_server_that_is_down_is_not_called_an_expired_session()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The child's credential directory rules are Unix-only here.");

        // 503 from the token endpoint says nothing about the session: telling someone to sign in again during an outage is wrong.
        using var server = Server(503, "service unavailable");
        using var bella = new BellaProcess();

        var (exit, _, stderr, said) = await Run(server, bella, "projects", "list");

        Assert.True(server.CountOf("POST", TokenPath) >= 1, stderr);
        Assert.NotEqual(0, exit);
        Assert.DoesNotContain(SessionExpiredException.Sentence, said, StringComparison.Ordinal);
    }
}
