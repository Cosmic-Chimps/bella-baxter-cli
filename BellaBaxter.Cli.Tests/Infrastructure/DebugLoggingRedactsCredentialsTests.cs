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

    // ── #2002 — key material: every key-bearing header, in both directions, by list and by rule ──────────────────────────

    private const string WrappedDek = "wrapped-dek-marker-2002";
    private const string WrappedSharedDek = "wrapped-shared-dek-marker-2002";
    private const string LeaseId = "lease-id-marker-2002";
    private const string CallProof = "call-proof-marker-2002";
    private const string ApiKey = "api-key-marker-2002";
    private const string VaultToken = "vault-token-marker-2002";
    private const string ByRuleOnly = "by-rule-only-marker-2002";
    private const string ByRuleKeyMaterial = "x-new-device-key-marker-2002";
    private const string BodyDek = "body-dek-marker-2002";
    private const string BodyToken = "body-token-marker-2002";
    private const string BodyText = "body-text-marker-2002";

    private static readonly string[] KeyHeaderValues =
        [WrappedDek, WrappedSharedDek, LeaseId, CallProof, ApiKey, VaultToken, ByRuleOnly, ByRuleKeyMaterial];

    private sealed class KeyBearing(HttpStatusCode status, string? body = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(status);
            // the response side of the key headers: a wrapped DEK, its shared twin, a lease, and one only the RULE catches
            response.Headers.TryAddWithoutValidation("X-Bella-Wrapped-Dek", WrappedDek);
            response.Headers.TryAddWithoutValidation("X-Bella-Wrapped-Shared-Dek", WrappedSharedDek);
            response.Headers.TryAddWithoutValidation("X-Bella-Lease-Id", LeaseId);
            response.Headers.TryAddWithoutValidation("X-Bella-Lease-Expires", "2026-11-26T00:00:00Z");
            response.Headers.TryAddWithoutValidation("X-Some-Future-Dek-Header", ByRuleOnly);
            if (body is not null)
                response.Content = new StringContent(body);
            return Task.FromResult(response);
        }
    }

    private static async Task<string> DebugOutputAsync(HttpMessageHandler inner)
    {
        var previous = Environment.GetEnvironmentVariable("BELLA_BAXTER_DEBUG");
        var stderr = Console.Error;
        var captured = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable("BELLA_BAXTER_DEBUG", "1");
            Console.SetError(captured);

            using var client = new HttpClient(new DebugLoggingHandler { InnerHandler = inner });
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.example.test/api/v1/secrets");
            // the request side: lease, call proof, invoke key, vault token, a header on no list whose name says "device key"
            request.Headers.TryAddWithoutValidation("X-Bella-Lease-Id", LeaseId);
            request.Headers.TryAddWithoutValidation("X-Bella-Wrapped-Dek", WrappedDek);
            request.Headers.TryAddWithoutValidation("X-Bella-Call-Proof", CallProof);
            request.Headers.TryAddWithoutValidation("X-Bella-Api-Key", ApiKey);
            request.Headers.TryAddWithoutValidation("X-Vault-Token", VaultToken);
            request.Headers.TryAddWithoutValidation("X-New-Device-Key", ByRuleKeyMaterial);
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
    public async Task Key_bearing_headers_are_masked_in_both_directions_by_list_and_by_rule()
    {
        var output = await DebugOutputAsync(new KeyBearing(HttpStatusCode.OK));

        foreach (var value in KeyHeaderValues)
            Assert.DoesNotContain(value, output);
        foreach (var name in new[]
        {
            "X-Bella-Wrapped-Dek", "X-Bella-Wrapped-Shared-Dek", "X-Bella-Lease-Id", "X-Bella-Call-Proof", "X-Bella-Api-Key",
            "X-Vault-Token", "X-New-Device-Key", "X-Some-Future-Dek-Header",
        })
            Assert.Contains($"{name}: ***", output);

        // Not key material: still visible, which is what the output is for.
        Assert.Contains("X-Bella-Timestamp: 1759400000", output);
        Assert.Contains("X-E2E-Public-Key: public-key-is-public", output);
        Assert.Contains("X-Bella-Lease-Expires: 2026-11-26T00:00:00Z", output);
    }

    [Theory]
    [InlineData("X-Anything-Key")]
    [InlineData("x-whatever-TOKEN-v2")]
    [InlineData("X-Client-Secret")]
    [InlineData("X-Data-Dek")]
    [InlineData("X-Request-Signature")]
    [InlineData("X-Session-Cookie")]
    [InlineData("Proxy-Authorization")]
    [InlineData("X-Wrapped-Thing")]
    public void The_rule_masks_a_name_that_says_what_it_carries(string name) =>
        Assert.True(DebugLoggingHandler.IsSensitiveHeader(name));

    [Theory]
    [InlineData("X-Bella-Timestamp")]
    [InlineData("X-User-Role")]
    [InlineData("X-Bella-Lease-Expires")]
    [InlineData("Content-Type")]
    [InlineData("X-E2E-Public-Key")]
    public void A_name_that_carries_no_key_is_not_masked(string name) =>
        Assert.False(DebugLoggingHandler.IsSensitiveHeader(name));

    [Fact]
    public async Task A_failure_body_is_logged_with_its_key_fields_masked()
    {
        var body = $$"""{"title":"Forbidden","detail":"{{BodyText}}","wrappedDek":"{{BodyDek}}","nested":{"access_token":"{{BodyToken}}"},"items":[{"signingSecret":"{{BodyToken}}"}]}""";

        var output = await DebugOutputAsync(new KeyBearing(HttpStatusCode.Forbidden, body));

        Assert.Contains(BodyText, output);          // the diagnostic part survives
        Assert.DoesNotContain(BodyDek, output);
        Assert.DoesNotContain(BodyToken, output);
        Assert.Contains("\"wrappedDek\":\"***\"", output);
    }

    [Fact]
    public async Task A_failure_body_that_is_not_json_has_its_name_value_pairs_masked()
    {
        var output = await DebugOutputAsync(new KeyBearing(HttpStatusCode.BadGateway, $"upstream said refresh_token={BodyToken}&detail={BodyText}"));

        Assert.DoesNotContain(BodyToken, output);
        Assert.Contains(BodyText, output);
    }

    [Fact]
    public async Task A_success_body_is_never_logged()
    {
        var output = await DebugOutputAsync(new KeyBearing(HttpStatusCode.OK, $$"""{"wrappedDek":"{{BodyDek}}","plain":"{{BodyText}}"}"""));

        Assert.DoesNotContain(BodyDek, output);
        Assert.DoesNotContain(BodyText, output);
    }

    [Fact]
    public void The_Go_SDKs_masked_set_is_masked_here_too()
    {
        // Two clients, one rule: the Go SDK masked these headers while the CLI printed them (#1053). The CLI now masks MORE
        // (#2002), so the Go set must be a subset of what the CLI hides, never the other way round.
        var go = MonorepoFile("apps/sdk/go/bellabaxter/transport.go");
        Assert.SkipWhen(go is null, "not a monorepo checkout — the Go SDK is not present");

        var block = Regex.Match(go!, @"var maskedHeaders = map\[string\]bool\{(?<body>[^}]*)\}");
        Assert.True(block.Success, "maskedHeaders not found in transport.go");
        var goSet = Regex.Matches(block.Groups["body"].Value, "\"([^\"]+)\"\\s*:\\s*true")
            .Select(m => m.Groups[1].Value)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.NotEmpty(goSet);
        Assert.All(goSet, name => Assert.True(DebugLoggingHandler.IsSensitiveHeader(name), $"{name} is masked by the Go SDK but not by the CLI"));
    }

    [Fact]
    public void The_Go_SDKs_debug_masking_names_the_same_rule_as_the_CLI()
    {
        // The rule words are one list in two languages; a word added on one side only is a header one client prints.
        var go = MonorepoFile("apps/sdk/go/bellabaxter/transport.go");
        Assert.SkipWhen(go is null, "not a monorepo checkout — the Go SDK is not present");

        var block = Regex.Match(go!, @"var sensitiveNameParts = \[\]string\{(?<body>[^}]*)\}");
        Assert.True(block.Success, "sensitiveNameParts not found in transport.go");
        var goParts = Regex.Matches(block.Groups["body"].Value, "\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToArray();

        // The CLI's own list, not a copy of it: a word added to either side alone fails here.
        Assert.Equal(DebugLoggingHandler.SensitiveNameParts.Order(), goParts.Order());
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
