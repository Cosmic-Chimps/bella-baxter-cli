using System.Text;
using BellaBaxter.Client.Models;
using Microsoft.Kiota.Serialization.Json;

namespace BellaBaxter.Cli.Tests.Commands.Issue;

/// <summary>
/// Pilot F2: <c>bella issue</c> reported "API returned an empty token" for every successful call.
/// The command deserialized the response into a hand-rolled DTO reading <c>token</c>, but
/// <c>CreatedApiKeyResponse</c>'s member is <c>apiKey</c> — so the token was always null and the
/// command exited 1 having already minted a real credential on the server.
///
/// These tests pin the two wire names the old hand-rolled DTOs got wrong. They deliberately go
/// through Kiota's own serializers rather than System.Text.Json: the point is that the CLI and the
/// generated client agree, not that some other JSON shape round-trips.
/// </summary>
public class IssueResponseWireNameTests
{
    [Fact]
    public async Task CreatedApiKeyResponse_reads_the_token_from_apiKey()
    {
        const string body = """
            {
              "success": true,
              "message": "Scoped token issued. Store it — it will not be shown again.",
              "apiKey": "bax-0123456789abcdef0123456789abcdef-aabbccddeeff00112233445566778899aabbccddeeff0011",
              "keyPrefix": "bax-abcd123",
              "id": "01234567-89ab-cdef-0123-456789abcdef",
              "expiresAt": "2026-09-08T12:00:00Z"
            }
            """;

        var response = await DeserializeAsync(body);

        Assert.NotNull(response);
        Assert.False(string.IsNullOrEmpty(response!.ApiKey));
        Assert.StartsWith("bax-", response.ApiKey);
        Assert.Equal("bax-abcd123", response.KeyPrefix);
        Assert.Equal("01234567-89ab-cdef-0123-456789abcdef", response.Id);
        Assert.Equal(
            DateTimeOffset.Parse("2026-09-08T12:00:00Z"),
            response.ExpiresAt!.Value.ToUniversalTime()
        );
    }

    [Fact]
    public async Task CreatedApiKeyResponse_has_no_token_member_to_read()
    {
        // The old DTO's `token` is not part of the contract at all: a body carrying only `token`
        // deserializes to an empty ApiKey, which is exactly the pilot's symptom.
        var response = await DeserializeAsync("""{ "token": "bax-would-have-worked" }""");

        Assert.NotNull(response);
        Assert.True(string.IsNullOrEmpty(response!.ApiKey));
    }

    [Fact]
    public void IssueEnvironmentTokenRequest_sends_reason_not_clientName()
    {
        var json = Serialize(
            new IssueEnvironmentTokenRequest
            {
                Scopes = ["stripe", "payment"],
                TtlMinutes = 15,
                Reason = "ansible-run",
            }
        );

        Assert.Contains("\"reason\"", json);
        Assert.Contains("ansible-run", json);
        Assert.DoesNotContain("clientName", json);
        Assert.Contains("\"ttlMinutes\"", json);
        Assert.Contains("\"scopes\"", json);
    }

    private static async Task<CreatedApiKeyResponse?> DeserializeAsync(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var node = await new JsonParseNodeFactory().GetRootParseNodeAsync(
            "application/json",
            stream,
            TestContext.Current.CancellationToken
        );
        return node.GetObjectValue(CreatedApiKeyResponse.CreateFromDiscriminatorValue);
    }

    private static string Serialize(IssueEnvironmentTokenRequest request)
    {
        using var writer = new JsonSerializationWriterFactory().GetSerializationWriter(
            "application/json"
        );
        writer.WriteObjectValue(null, request);
        using var reader = new StreamReader(writer.GetSerializedContent());
        return reader.ReadToEnd();
    }
}
