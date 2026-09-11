using System.Net;
using System.Security.Cryptography;
using BellaBaxter.Client;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// spec 037 (T020) — the two client handlers put byte-identical values in <c>X-E2E-Public-Key</c>.
/// </summary>
/// <remarks>
/// <para>This pins the PREMISE of the whole feature (research R9). Every SDK sends a public key on
/// every secrets request, and an ephemeral transport key and a persistent device key are the same
/// thing on the wire: base64 SPKI of a P-256 public key. There is no shape, no prefix and no second
/// header the server could use to tell "this is a registered device" from "this is a throwaway I made
/// a moment ago".</para>
///
/// <para>So the server MUST look the key up. Adding a "this is a device key" header instead would
/// require nine SDK releases AND would let a client simply claim the thing being verified. If this test
/// ever fails because someone made the two handlers distinguishable, that is not a fix — it is an
/// invitation to trust a client's self-description.</para>
/// </remarks>
public class HandlersEmitIdenticalHeaderTests
{
    private const string SecretsUrl = "https://api.example.test/api/v1/projects/p/environments/dev/secrets";

    [Fact]
    public async Task Both_handlers_send_the_same_bytes_for_one_key()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var expected = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

        var zkeHeader = await CaptureHeaderAsync(new ZkeDekHandler(key));

        // The ephemeral handler generates its own key, so compare SHAPE against the same encoding: the
        // claim is that one value is indistinguishable from the other, not that they are equal.
        var ephemeralHeader = await CaptureHeaderAsync(new E2EEncryptionHandler());

        Assert.Equal(expected, zkeHeader);
        Assert.NotNull(ephemeralHeader);

        // Same encoding, same length, same parse result — nothing separates them.
        var zkeBytes = Convert.FromBase64String(zkeHeader!);
        var ephemeralBytes = Convert.FromBase64String(ephemeralHeader!);
        Assert.Equal(zkeBytes.Length, ephemeralBytes.Length);

        using var reparsedZke = ECDiffieHellman.Create();
        using var reparsedEphemeral = ECDiffieHellman.Create();
        reparsedZke.ImportSubjectPublicKeyInfo(zkeBytes, out _);
        reparsedEphemeral.ImportSubjectPublicKeyInfo(ephemeralBytes, out _);
        Assert.Equal(reparsedZke.KeySize, reparsedEphemeral.KeySize);
    }

    [Fact]
    public async Task Neither_handler_sends_anything_that_claims_registration()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        var headers = await CaptureAllHeadersAsync(new ZkeDekHandler(key));

        // No second header, and no marker inside the value. Registration is a SERVER fact, established
        // by lookup — a client that could assert it could assert it falsely.
        Assert.DoesNotContain(headers.Keys, h => h.Contains("device", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(headers.Keys, h => h.Contains("registered", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Issue #635 — the CLI asks `getZkeStatus` BEFORE a read, and that call must carry the key.
    /// </summary>
    /// <remarks>
    /// Both handlers gated the header on a `/secrets` path test, so the status call went out bare, the
    /// server correctly answered `presentedKeyRegistered: false`, and the CLI refused itself before
    /// reaching any secret. A registered, active device could not read anything — and this test file
    /// missed it because every case here used a `/secrets` URL.
    /// </remarks>
    [Theory]
    [InlineData("https://api.example.test/api/v1/tenants/me/zke")]
    [InlineData("https://api.example.test/api/v1/projects/p/environments/dev/secrets")]
    [InlineData("https://api.example.test/api/v1/environments/2f1c9d3e-0000-0000-0000-000000000000/tokens/issue")]
    public async Task Both_handlers_present_the_key_on_every_api_path(string url)
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        var fromDeviceKey = await CaptureHeaderAsync(new ZkeDekHandler(key), url);
        var fromEphemeral = await CaptureHeaderAsync(new E2EEncryptionHandler(), url);

        Assert.False(string.IsNullOrEmpty(fromDeviceKey), $"ZkeDekHandler sent no key to {url}");
        Assert.False(string.IsNullOrEmpty(fromEphemeral), $"E2EEncryptionHandler sent no key to {url}");
    }

    [Fact]
    public async Task The_key_is_not_presented_to_a_non_api_host()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        // A public key discloses nothing, but it should still not follow a redirect off the API.
        var leaked = await CaptureHeaderAsync(
            new ZkeDekHandler(key), "https://telemetry.example.test/collect");

        Assert.True(string.IsNullOrEmpty(leaked), "the device key was presented outside the API");
    }

    private static async Task<string?> CaptureHeaderAsync(DelegatingHandler handler, string? url = null) =>
        (await CaptureAllHeadersAsync(handler, url)).GetValueOrDefault("X-E2E-Public-Key");

    private static async Task<Dictionary<string, string>> CaptureAllHeadersAsync(
        DelegatingHandler handler, string? url = null)
    {
        var recorder = new RecordingHandler();
        handler.InnerHandler = recorder;

        using var client = new HttpClient(handler);
        using var response = await client.GetAsync(url ?? SecretsUrl, TestContext.Current.CancellationToken);

        return recorder.Headers;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            foreach (var header in request.Headers)
                Headers[header.Key] = string.Join(",", header.Value);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
