using System.Text;
using BellaBaxter.Client.Models;
using Microsoft.Kiota.Serialization.Json;

namespace BellaBaxter.Cli.Tests.Commands.Auth;

/// <summary>
/// spec 037 (T019) — the device wire names, pinned through the GENERATED models.
/// </summary>
/// <remarks>
/// <para>The pilot F2 lesson: <c>bella issue</c> read <c>token</c> where the server sent <c>apiKey</c>,
/// so a successful call reported failure and nobody noticed until it mattered. The failure mode here
/// would be quieter and worse — <c>registered</c> read as null means false, so a properly registered
/// device would be told to run <c>bella auth setup</c> forever, and <c>enforced</c> read as null means
/// false, so the CLI would sail past a policy that is switched on.</para>
///
/// <para>These go through Kiota's own serializer, not System.Text.Json: the claim is that the CLI and
/// the generated client agree, not that some other JSON shape round-trips.</para>
/// </remarks>
public class DeviceWireNameTests
{
    [Fact]
    public async Task ZkeStatusResponse_reads_enforced_and_the_presented_keys_registration()
    {
        const string body = """
            {
              "enforced": true,
              "keyPresented": true,
              "presentedKeyFingerprint": "SHA256:mF2v7Q9x0Zk1a2B3c4D5e6F7g8H9i0J1k2L3m4N5o6P",
              "presentedKeyRegistered": true,
              "presentedKeyLabel": "MacBook Pro"
            }
            """;

        var status = await DeserializeAsync(body, ZkeStatusResponse.CreateFromDiscriminatorValue);

        Assert.NotNull(status);
        Assert.True(status!.Enforced);
        Assert.True(status.KeyPresented);
        Assert.True(status.PresentedKeyRegistered);
        Assert.Equal("MacBook Pro", status.PresentedKeyLabel);
        Assert.StartsWith("SHA256:", status.PresentedKeyFingerprint);
    }

    [Fact]
    public async Task The_status_shape_is_FLAT_because_a_nested_nullable_object_would_not_deserialise()
    {
        // The defect this shape prevents, pinned as a test rather than as a comment: with the nested
        // form the server used to emit (`presentedKey: { registered: true }`), Kiota generates a
        // composed type selected by a discriminator that does not exist, so `registered` reads false —
        // and a properly registered device is told forever to run `bella auth setup`. If someone
        // re-nests it, the assertions above stop compiling, which is the point.
        var status = await DeserializeAsync(
            """{ "enforced": true, "presentedKey": { "registered": true } }""",
            ZkeStatusResponse.CreateFromDiscriminatorValue);

        Assert.NotNull(status);
        // Not merely false — ABSENT. The nested field is not read at all, which is exactly how a
        // registered device would have been reported as unregistered.
        Assert.Null(status!.PresentedKeyRegistered);
    }

    [Fact]
    public async Task ZkeStatusResponse_with_no_presented_key_is_not_an_error()
    {
        // A request that carried no key at all: the CLI must read this as "no key", not crash.
        var status = await DeserializeAsync(
            """{ "enforced": false, "keyPresented": false, "presentedKeyFingerprint": null, "presentedKeyRegistered": false }""",
            ZkeStatusResponse.CreateFromDiscriminatorValue);

        Assert.NotNull(status);
        Assert.False(status!.Enforced);
        Assert.False(status.KeyPresented);
        Assert.Null(status.PresentedKeyFingerprint);
    }

    [Fact]
    public async Task An_unregistered_key_reads_as_registered_false_and_not_as_absent()
    {
        var status = await DeserializeAsync(
            """
            { "enforced": true, "keyPresented": true, "presentedKeyFingerprint": "SHA256:abc", "presentedKeyRegistered": false }
            """,
            ZkeStatusResponse.CreateFromDiscriminatorValue);

        Assert.True(status!.KeyPresented);
        Assert.False(status.PresentedKeyRegistered);
        Assert.Equal("SHA256:abc", status.PresentedKeyFingerprint);
    }

    [Fact]
    public async Task ZkeDeviceResponse_reads_the_fields_the_devices_table_prints()
    {
        const string body = """
            {
              "id": "01234567-89ab-cdef-0123-456789abcdef",
              "fingerprint": "SHA256:mF2v7Q9x0Zk",
              "label": "build-agent-3",
              "registeredAt": "2026-09-08T12:00:00Z",
              "registeredBy": { "userId": "11111111-2222-3333-4444-555555555555", "displayName": "Ada Lovelace" },
              "state": "active",
              "revokedAt": null,
              "revokedBy": null,
              "revocationReason": null,
              "lastUsedAt": "2026-09-08T13:30:00Z"
            }
            """;

        var device = await DeserializeAsync(body, ZkeDeviceResponse.CreateFromDiscriminatorValue);

        Assert.NotNull(device);
        Assert.Equal("SHA256:mF2v7Q9x0Zk", device!.Fingerprint);
        Assert.Equal("build-agent-3", device.Label);
        Assert.Equal("active", device.State);
        Assert.Equal("Ada Lovelace", device.RegisteredBy?.DisplayName);
        Assert.Equal(Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"), device.Id);
        Assert.NotNull(device.LastUsedAt);
    }

    [Fact]
    public async Task A_revoked_device_reads_its_state_and_reason()
    {
        var device = await DeserializeAsync(
            """
            {
              "id": "01234567-89ab-cdef-0123-456789abcdef",
              "fingerprint": "SHA256:gone",
              "state": "revoked",
              "revokedAt": "2026-09-08T14:00:00Z",
              "revocationReason": "RevokedByAdmin",
              "lastUsedAt": null
            }
            """,
            ZkeDeviceResponse.CreateFromDiscriminatorValue);

        Assert.Equal("revoked", device!.State);
        Assert.Equal("RevokedByAdmin", device.RevocationReason);
        Assert.Null(device.LastUsedAt);
    }

    [Fact]
    public void RegisterZkeDeviceRequest_sends_publicKey_and_label()
    {
        var json = Serialize(new RegisterZkeDeviceRequest { PublicKey = "MFkwEw...", Label = "MacBook Pro" });

        Assert.Contains("\"publicKey\"", json);
        Assert.Contains("\"label\"", json);
        Assert.Contains("MacBook Pro", json);
        // The fingerprint is computed by the SERVER and must never be sent (FR-025).
        Assert.DoesNotContain("fingerprint", json);
    }

    private static async Task<T?> DeserializeAsync<T>(
        string json,
        Microsoft.Kiota.Abstractions.Serialization.ParsableFactory<T> factory)
        where T : Microsoft.Kiota.Abstractions.Serialization.IParsable
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var node = await new JsonParseNodeFactory().GetRootParseNodeAsync(
            "application/json", stream, TestContext.Current.CancellationToken);
        return node.GetObjectValue(factory);
    }

    private static string Serialize(RegisterZkeDeviceRequest request)
    {
        using var writer = new JsonSerializationWriterFactory().GetSerializationWriter("application/json");
        writer.WriteObjectValue(null, request);
        using var reader = new StreamReader(writer.GetSerializedContent());
        return reader.ReadToEnd();
    }
}
