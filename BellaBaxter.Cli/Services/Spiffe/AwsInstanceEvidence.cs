using System.Net;
using System.Text;
using System.Text.Json;

namespace BellaCli.Services.Spiffe;

// Spec 065 (US2, research R9) — reading this EC2 instance's identity document for `--node-type aws-iid`.
//
// IMDSv2 ONLY. A session token is obtained with a PUT and presented on every read. IMDSv1 is never tried, even as a
// fallback: IMDSv1 is what a server-side request forgery reaches, and an instance that requires IMDSv2 has said so
// deliberately. The most common failure is a container whose PUT response cannot reach it (the instance's
// HttpPutResponseHopLimit is 1), and the error names exactly that.
//
// NEVER AUTO-DETECTED. Probing 169.254.169.254 off EC2 costs a timeout on every machine; the operator names the node
// type instead. The document is read on EVERY attestation, as the Kubernetes token is, so a restart's new document
// (with its newer pendingTime) is what the next attestation presents.
//
// NO PROXY. Instance metadata is link-local and must never be sent through HTTP(S)_PROXY.

/// <summary>What the document says about this instance, read locally and NOT verified. For display only.</summary>
public sealed record AwsInstanceFactsView(string? Account, string? Region, string? InstanceId, string? PendingTime);

/// <summary>The instance metadata service could not supply the document. The message is operator-facing.</summary>
public sealed class AwsInstanceEvidenceException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class AwsInstanceEvidence(HttpClient imds)
{
    /// <summary>The named HttpClient for instance metadata: no proxy, link-local base address, short timeout.</summary>
    public const string HttpClientName = "bella-imds";

    public static readonly Uri BaseAddress = new("http://169.254.169.254");
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The handler the named client must use: IMDS is link-local and must never go through a proxy.</summary>
    public static HttpMessageHandler CreateHandler() => new SocketsHttpHandler { UseProxy = false };

    /// <summary>The evidence envelope Bella's attestor verifies: base64url(JSON <c>{document, signature}</c>).</summary>
    public async Task<string> ReadEnvelopeAsync(CancellationToken ct)
    {
        var (document, signature) = await ReadAsync(ct).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(new { document, signature });
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>The document's own claims, for <c>whoami</c> and the startup line. Never the signature.</summary>
    public async Task<AwsInstanceFactsView> ReadFactsAsync(CancellationToken ct)
    {
        var (document, _) = await ReadAsync(ct).ConfigureAwait(false);
        try
        {
            using var json = JsonDocument.Parse(document);
            string? Field(string name) =>
                json.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return new AwsInstanceFactsView(Field("accountId"), Field("region"), Field("instanceId"), Field("pendingTime"));
        }
        catch (JsonException ex)
        {
            throw new AwsInstanceEvidenceException("The instance identity document is not valid JSON.", ex);
        }
    }

    private async Task<(string Document, string Signature)> ReadAsync(CancellationToken ct)
    {
        var token = await Call(HttpMethod.Put, "/latest/api/token", null, ct, isTokenRequest: true).ConfigureAwait(false);
        var document = await Call(HttpMethod.Get, "/latest/dynamic/instance-identity/document", token, ct).ConfigureAwait(false);
        var signature = await Call(HttpMethod.Get, "/latest/dynamic/instance-identity/signature", token, ct).ConfigureAwait(false);
        return (document, signature.Replace("\n", string.Empty).Replace("\r", string.Empty).Trim());
    }

    private async Task<string> Call(HttpMethod method, string path, string? token, CancellationToken ct, bool isTokenRequest = false)
    {
        using var request = new HttpRequestMessage(method, new Uri(BaseAddress, path));
        if (isTokenRequest)
            request.Headers.Add("X-aws-ec2-metadata-token-ttl-seconds", "60");
        else
            request.Headers.Add("X-aws-ec2-metadata-token", token);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CallTimeout);
        HttpResponseMessage response;
        try
        {
            response = await imds.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new AwsInstanceEvidenceException(Unreachable(isTokenRequest), ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new AwsInstanceEvidenceException(isTokenRequest
                    ? Unreachable(isTokenRequest: true) + $" (HTTP {(int)response.StatusCode})"
                    : $"The instance metadata service refused {path} (HTTP {(int)response.StatusCode}).");
            }
            var body = (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
            if (body.Length == 0)
                throw new AwsInstanceEvidenceException($"The instance metadata service returned nothing for {path}.");
            return body;
        }
    }

    private static string Unreachable(bool isTokenRequest) => isTokenRequest
        ? "Could not obtain an IMDSv2 session token from the instance metadata service. Is this an EC2 instance? "
          + "In a container, the instance's HttpPutResponseHopLimit must be at least 2. The agent never falls back "
          + "to IMDSv1."
        : "The instance metadata service did not answer.";
}
