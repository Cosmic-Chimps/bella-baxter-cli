using System.Net;
using System.Text.RegularExpressions;
using BellaCli.Infrastructure;

namespace BellaBaxter.Cli.Tests.Infrastructure;

/// <summary>
/// Issue #1053 — <c>BELLA_BAXTER_DEBUG=1</c> shows which headers went out and came back, never a
/// credential's value.
/// </summary>
/// <remarks>
/// <para>The handler printed every header value to stderr, <c>Authorization</c> (the bearer token) and
/// <c>X-Bella-Signature</c> (the HMAC request signature) included, while declaring a
/// <c>TrackedRequestHeaders</c> list that nothing read. Whoever reads that stderr — a log aggregator, a
/// shared CI runner, a pasted support ticket — got a reusable credential.</para>
/// <para>The masked set mirrors the Go SDK's <c>maskedHeaders</c> (<c>apps/sdk/go/bellabaxter/transport.go</c>),
/// and a source test holds the two equal so the clients cannot drift apart again.</para>
/// </remarks>
[Collection(DebugLoggingEnvironmentCollection.Name)]
public class DebugLoggingRedactsCredentialsTests
{
    private const string Bearer = "eyJ-bearer-marker-1053";
    private const string Signature = "sig-marker-1053";
    private const string KeyId = "keyid-marker-1053";
    private const string Cookie = "cookie-marker-1053";
    private const string SetCookie = "setcookie-marker-1053";

    private sealed class Stub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.Add("Set-Cookie", $"session={SetCookie}");
            response.Headers.Add("X-User-Role", "ADMIN");
            return Task.FromResult(response);
        }
    }

    private static async Task<string> DebugOutputOfOneRequestAsync()
    {
        var previous = Environment.GetEnvironmentVariable("BELLA_BAXTER_DEBUG");
        var stderr = Console.Error;
        var captured = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable("BELLA_BAXTER_DEBUG", "1");
            Console.SetError(captured);

            using var client = new HttpClient(new DebugLoggingHandler { InnerHandler = new Stub() });
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/api/v1/projects");
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Bearer}");
            request.Headers.TryAddWithoutValidation("X-Bella-Signature", Signature);
            request.Headers.TryAddWithoutValidation("X-Bella-Key-Id", KeyId);
            request.Headers.TryAddWithoutValidation("Cookie", $"a={Cookie}");
            request.Headers.TryAddWithoutValidation("X-Bella-Timestamp", "1759400000");
            request.Headers.TryAddWithoutValidation("X-E2E-Public-Key", "public-key-is-public");
            using var response = await client.SendAsync(request);
        }
        finally
        {
            Console.SetError(stderr);
            Environment.SetEnvironmentVariable("BELLA_BAXTER_DEBUG", previous);
        }
        return captured.ToString();
    }

    [Fact]
    public async Task Credential_headers_show_their_name_and_never_their_value()
    {
        var output = await DebugOutputOfOneRequestAsync();

        foreach (var name in new[] { "Authorization", "X-Bella-Signature", "X-Bella-Key-Id", "Cookie", "Set-Cookie" })
            Assert.Contains($"{name}: ***", output);
        foreach (var secret in new[] { Bearer, Signature, KeyId, Cookie, SetCookie })
            Assert.DoesNotContain(secret, output);
    }

    [Fact]
    public async Task Headers_that_are_not_credentials_still_show_their_value()
    {
        // The point of debug output: a timestamp, a PUBLIC key and a role are what a person reads it for.
        var output = await DebugOutputOfOneRequestAsync();

        Assert.Contains("X-Bella-Timestamp: 1759400000", output);
        Assert.Contains("X-E2E-Public-Key: public-key-is-public", output);
        Assert.Contains("X-User-Role: ADMIN", output);
    }

    [Fact]
    public void The_masked_set_is_the_Go_SDKs_masked_set()
    {
        // Two clients, one rule: the Go SDK masked these headers while the CLI printed them (#1053).
        var go = MonorepoFile("apps/sdk/go/bellabaxter/transport.go");
        Assert.SkipWhen(go is null, "not a monorepo checkout — the Go SDK is not present");

        var block = Regex.Match(go!, @"var maskedHeaders = map\[string\]bool\{(?<body>[^}]*)\}");
        Assert.True(block.Success, "maskedHeaders not found in transport.go");
        var goSet = Regex.Matches(block.Groups["body"].Value, "\"([^\"]+)\"\\s*:\\s*true")
            .Select(m => m.Groups[1].Value)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.NotEmpty(goSet);
        Assert.Equal(goSet, DebugLoggingHandler.MaskedHeaders.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static string? MonorepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "apps", "cli-dotnet")))
            dir = dir.Parent;
        if (dir is null) return null;
        var path = Path.Combine(dir.FullName, relative);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }
}

/// <summary>Tests that set BELLA_BAXTER_DEBUG and swap Console.Error run alone.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DebugLoggingEnvironmentCollection
{
    public const string Name = "debug-logging-environment";
}
