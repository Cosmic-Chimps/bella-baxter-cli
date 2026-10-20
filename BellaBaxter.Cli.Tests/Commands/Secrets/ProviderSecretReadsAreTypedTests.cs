using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BellaBaxter.Client;
using BellaBaxter.Crypto;
using BellaCli.Infrastructure;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;

namespace BellaBaxter.Cli.Tests.Commands.Secrets;

/// <summary>
/// #1187 — <c>bella secrets get --provider</c>, <c>bella secrets list</c> and <c>bella agent</c> read a provider's
/// secrets through the TYPED <c>listSecrets</c> response (<c>List&lt;SecretItem&gt;</c>), plaintext and decrypted alike.
/// </summary>
/// <remarks>
/// <para>They used to read <c>AdditionalData["secrets"]</c>: the OpenAPI document declared the E2EE envelope as that read's
/// body, so the typed model came back empty, and the .NET SDK papered over it by rewriting every decrypted item list to
/// <c>{"secrets":{"KEY":"VALUE"},"version":0}</c> — a body the real server never sends and a shape a typed client cannot
/// parse. A CLI that only understood the rewrite would have shown NO secrets against a plaintext answer from the real
/// API's non-E2EE path, which is the case this pins beside the decrypted one.</para>
///
/// <para>The client here is the real generated <see cref="BellaClient"/> over the SDK's own handlers and a stub that
/// serializes what the API serializes (<c>SecretItem</c> records), so the test follows the same path the commands do.</para>
/// </remarks>
public class ProviderSecretReadsAreTypedTests
{
    private const string Origin = "https://api.example.test";
    private static readonly JsonSerializerOptions Wire = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private const string Body =
        """[{"key":"DATABASE_URL","value":"postgres://db/app","description":null,"createdAt":"2026-01-01T00:00:00Z","updatedAt":null,"type":"string","ignoreInScan":false},"""
        + """{"key":"EMPTY","value":null,"description":null,"createdAt":null,"updatedAt":null}]""";

    private sealed class Server(string plaintext) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var key = request.Headers.TryGetValues("X-E2E-Public-Key", out var v) ? v.Single() : null;
            var body = key is null
                ? plaintext
                : JsonSerializer.Serialize(EciesAlgorithm.Encrypt(Encoding.UTF8.GetBytes(plaintext), key), Wire);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static BellaClient ClientWith(bool e2ee, string plaintext)
    {
        HttpMessageHandler chain = e2ee
            ? new E2EEncryptionHandler { InnerHandler = new Server(plaintext) }
            : new Server(plaintext);
        return new BellaClient(new HttpClientRequestAdapter(
            new AnonymousAuthenticationProvider(), httpClient: new HttpClient(chain)) { BaseUrl = Origin });
    }

    [Theory]
    [InlineData(false)] // no key presented: the plaintext over TLS
    [InlineData(true)]  // a key presented: the envelope, opened by the SDK before the typed client parses it
    public async Task A_providers_secrets_read_as_a_key_to_value_map(bool e2ee)
    {
        var values = await ProviderSecretValues.ReadAsync(ClientWith(e2ee, Body), "p", "e", "vault", CancellationToken.None);

        Assert.NotNull(values);
        Assert.Equal("postgres://db/app", values["DATABASE_URL"]);
        // A null value is an empty string, as the old reader had it; the key is still listed.
        Assert.Equal("", values["EMPTY"]);
        Assert.Equal(2, values.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_provider_with_no_secrets_reads_as_an_empty_map_not_as_unreadable(bool e2ee)
    {
        // Distinguishes "nothing there" (an empty list) from "could not read" (no body), which `bella secrets list` reports
        // differently.
        var values = await ProviderSecretValues.ReadAsync(ClientWith(e2ee, "[]"), "p", "e", "vault", CancellationToken.None);

        Assert.NotNull(values);
        Assert.Empty(values);
    }

    [Fact]
    public void The_map_is_read_from_the_typed_items_and_a_repeated_key_takes_the_last()
    {
        var items = new List<BellaBaxter.Client.Models.SecretItem>
        {
            new() { Key = "A", Value = "1" },
            new() { Key = "A", Value = "2" },
        };

        Assert.Equal("2", items.ToValueMap()["A"]);
        Assert.Empty(((List<BellaBaxter.Client.Models.SecretItem>?)null).ToValueMap());
    }

    [Fact]
    public void No_command_reads_the_old_secrets_map_out_of_AdditionalData()
    {
        // The rewrite that produced {"secrets":{...}} for the item reads is gone from the .NET SDK, so a command that still
        // looks for it finds nothing and reports an empty environment — silently. Three commands did.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "BellaBaxter.Cli")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var offenders = Directory.EnumerateFiles(Path.Combine(dir!.FullName, "BellaBaxter.Cli", "Commands"), "*.cs", SearchOption.AllDirectories)
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(f), @"AdditionalData[^;]*TryGetValue\(\s*""secrets"""))
            .Select(Path.GetFileName)
            .Order()
            .ToList();

        Assert.True(offenders.Count == 0, "These commands read AdditionalData[\"secrets\"], which no SDK produces any more: " + string.Join(", ", offenders));
    }
}
