using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using BellaCli.Services.Spiffe;
using BellaCli.Services.Spiffe.WorkloadApi;
using Grpc.Core;
using Grpc.Net.Client;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// Spec 001 T041 (US6, SC-008) — the SPIFFE Workload API, exercised over a real Unix socket by a real
/// gRPC client.
/// </summary>
/// <remarks>
/// <para><b>What this proves.</b> A client generated from the vendored upstream schema connects to the
/// agent's actual socket, over real HTTP/2, and gets back messages it can parse — including the DER
/// encodings the spec mandates, which is where a PEM-shaped mistake would otherwise sit undetected
/// until a `go-spiffe` workload failed somewhere else entirely.</para>
///
/// <para><b>What it does NOT prove, stated so nobody over-reads it.</b> Both ends are generated from
/// the same file by the same protoc, so this cannot catch an error in the vendored schema itself — if a
/// field number were wrong, both halves would be wrong together and agree perfectly. That risk is
/// managed by vendoring the file verbatim from upstream rather than transcribing it, and by the
/// remaining manual step in quickstart.md: pointing an actual `go-spiffe` client at the socket. This
/// test is the regression net; that pass is the interop proof.</para>
/// </remarks>
public class WorkloadApiConformanceTests
{
    private const string SpiffeId = "spiffe://acme/payments/prod/billing-service";
    private const string TrustDomain = "spiffe://acme";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Timeout = 30_000)]
    public async Task A_standard_client_fetches_the_X509_SVID_over_the_socket()
    {
        await using var fixture = await AgentFixture.StartAsync(SvidFactory.Create(SpiffeId));
        var client = fixture.Client;

        using var call = client.FetchX509SVID(new X509SVIDRequest(), fixture.Headers, cancellationToken: Ct);
        Assert.True(await call.ResponseStream.MoveNext(Ct));

        var response = call.ResponseStream.Current;
        var svid = Assert.Single(response.Svids);

        Assert.Equal(SpiffeId, svid.SpiffeId);

        // The fields are DER, not PEM. Parsing them with the BCL is the assertion: a base64 PEM body
        // would fail here, and in production it would instead fail inside somebody else's TLS stack.
        using var leaf = X509CertificateLoader.LoadCertificate(svid.X509Svid.ToByteArray());
        Assert.True(CarriesSpiffeUriSan(leaf, SpiffeId),
            "the delivered DER certificate does not carry the SPIFFE ID as a URI SAN");

        using var bundleCa = X509CertificateLoader.LoadCertificate(svid.Bundle.ToByteArray());
        Assert.True(bundleCa.Extensions
            .OfType<X509BasicConstraintsExtension>()
            .Any(e => e.CertificateAuthority), "the bundle should carry a CA certificate");
    }

    [Fact(Timeout = 30_000)]
    public async Task The_private_key_arrives_as_PKCS8_DER_and_matches_the_certificate()
    {
        // The single most damaging thing this could get wrong quietly: a key that parses but belongs to
        // a different certificate produces a TLS handshake failure with no useful message anywhere.
        await using var fixture = await AgentFixture.StartAsync(SvidFactory.Create(SpiffeId));

        using var call = fixture.Client.FetchX509SVID(
            new X509SVIDRequest(), fixture.Headers, cancellationToken: Ct);
        Assert.True(await call.ResponseStream.MoveNext(Ct));
        var svid = call.ResponseStream.Current.Svids[0];

        using var leaf = X509CertificateLoader.LoadCertificate(svid.X509Svid.ToByteArray());
        using var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(svid.X509SvidKey.ToByteArray(), out var read);

        Assert.Equal(svid.X509SvidKey.Length, read);

        // Sign with the delivered key, verify with the delivered certificate's public key. Nothing short
        // of this establishes that the pair belongs together.
        var payload = Encoding.UTF8.GetBytes("workload-api-conformance");
        var signature = rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        using var publicKey = leaf.GetRSAPublicKey()!;
        Assert.True(publicKey.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
            "the delivered private key does not match the delivered certificate");
    }

    [Fact(Timeout = 30_000)]
    public async Task The_bundle_stream_is_keyed_by_TRUST_DOMAIN_not_by_the_workloads_full_id()
    {
        // Keyed wrongly, this produces a response that parses perfectly and in which a standard client
        // finds no bundle: it looks up `spiffe://acme` and we wrote the whole workload id.
        await using var fixture = await AgentFixture.StartAsync(SvidFactory.Create(SpiffeId));

        using var call = fixture.Client.FetchX509Bundles(
            new X509BundlesRequest(), fixture.Headers, cancellationToken: Ct);
        Assert.True(await call.ResponseStream.MoveNext(Ct));

        var bundles = call.ResponseStream.Current.Bundles;
        Assert.True(bundles.ContainsKey(TrustDomain),
            $"expected key '{TrustDomain}', got: {string.Join(", ", bundles.Keys)}");

        using var ca = X509CertificateLoader.LoadCertificate(bundles[TrustDomain].ToByteArray());
        Assert.Contains("Acme", ca.Subject, StringComparison.Ordinal);
    }

    [Fact(Timeout = 30_000)]
    public async Task A_ROTATION_is_pushed_to_an_open_stream()
    {
        // FR-017, and the reason the API is streaming at all: a workload must pick up a new identity
        // without restarting. If this did not work, everything would look correct until the first
        // renewal and then every workload would be holding an expired certificate.
        var source = new ControllableSvidSource(SvidFactory.Create(SpiffeId, minutes: 10));
        await using var fixture = await AgentFixture.StartAsync(source);

        using var call = fixture.Client.FetchX509SVID(
            new X509SVIDRequest(), fixture.Headers, cancellationToken: Ct);

        Assert.True(await call.ResponseStream.MoveNext(Ct));
        var first = call.ResponseStream.Current.Svids[0].X509Svid.ToByteArray();

        // Rotate: a genuinely different certificate for the same identity.
        source.Next = SvidFactory.Create(SpiffeId, minutes: 10);
        await fixture.Agent.EnsureFreshAsync(DateTimeOffset.UtcNow.AddMinutes(9), Ct);

        Assert.True(await call.ResponseStream.MoveNext(Ct));
        var second = call.ResponseStream.Current.Svids[0].X509Svid.ToByteArray();

        Assert.NotEqual(Convert.ToBase64String(first), Convert.ToBase64String(second));
    }

    [Fact(Timeout = 30_000)]
    public async Task A_call_WITHOUT_the_required_security_header_is_refused()
    {
        // The SPIFFE spec requires `workload.spiffe.io: true`. It is the spec's guard against a browser
        // or a confused HTTP client being talked into driving the socket — accepting calls without it
        // works fine in testing and quietly widens what can reach a private key.
        await using var fixture = await AgentFixture.StartAsync(SvidFactory.Create(SpiffeId));

        using var call = fixture.Client.FetchX509SVID(
            new X509SVIDRequest(), new Metadata(), cancellationToken: Ct);

        var ex = await Assert.ThrowsAsync<RpcException>(async () => await call.ResponseStream.MoveNext(Ct));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact(Timeout = 30_000)]
    public async Task A_stream_with_NO_identity_yet_waits_instead_of_sending_an_empty_response()
    {
        // At startup the agent may not have attested. An empty X509SVIDResponse is a valid protobuf
        // message that a client reads as "you are entitled to nothing" — a permanent-looking answer to a
        // temporary state, and it would send workloads into a fail-closed path they cannot recover from.
        var source = new ControllableSvidSource(SvidFactory.Create(SpiffeId));
        await using var fixture = await AgentFixture.StartAsync(source, attestOnStart: false);

        using var call = fixture.Client.FetchX509SVID(
            new X509SVIDRequest(), fixture.Headers, cancellationToken: Ct);

        using var shortWait = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        shortWait.CancelAfter(TimeSpan.FromSeconds(2));

        // The stream stays open with nothing on it, so the WAIT is what times out — not a message
        // arriving. gRPC surfaces a client-side cancellation as RpcException(Cancelled) rather than a
        // bare OperationCanceledException, so that status IS the pass condition here: it says the call
        // was still in progress with nothing delivered.
        var cancelled = await Assert.ThrowsAsync<RpcException>(
            async () => await call.ResponseStream.MoveNext(shortWait.Token));
        Assert.Equal(StatusCode.Cancelled, cancelled.StatusCode);

        // And once an identity exists, a NEW stream serves it — the wait was not a broken stream.
        await fixture.Agent.EnsureFreshAsync(DateTimeOffset.UtcNow, Ct);
        using var second = fixture.Client.FetchX509SVID(
            new X509SVIDRequest(), fixture.Headers, cancellationToken: Ct);
        Assert.True(await second.ResponseStream.MoveNext(Ct));
    }

    [Fact(Timeout = 30_000)]
    public async Task FetchJWTSVID_returns_a_token_per_requested_audience()
    {
        await using var fixture = await AgentFixture.StartAsync(SvidFactory.Create(SpiffeId));

        var response = await fixture.Client.FetchJWTSVIDAsync(
            new JWTSVIDRequest { Audience = { "bella-api", "other-service" } },
            fixture.Headers, cancellationToken: Ct);

        Assert.Equal(2, response.Svids.Count);
        Assert.All(response.Svids, s => Assert.Equal(SpiffeId, s.SpiffeId));
        Assert.Contains(response.Svids, s => s.Svid.Contains("bella-api", StringComparison.Ordinal));
    }

    [Fact(Timeout = 30_000)]
    public async Task FetchJWTSVID_with_NO_audience_is_refused()
    {
        // The spec makes audience required, and an audience-less token is accepted by anything that
        // does not check — precisely the failure audience binding exists to prevent. So this is
        // InvalidArgument rather than a convenient default.
        await using var fixture = await AgentFixture.StartAsync(SvidFactory.Create(SpiffeId));

        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
            await fixture.Client.FetchJWTSVIDAsync(
                new JWTSVIDRequest(), fixture.Headers, cancellationToken: Ct));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact(Timeout = 30_000)]
    public async Task FetchJWTSVID_for_a_DIFFERENT_identity_is_refused_not_quietly_answered()
    {
        // A client asking for workload B and receiving workload A's token would authenticate as the
        // wrong thing and only discover it at the far end, as an authorisation failure with no clue.
        await using var fixture = await AgentFixture.StartAsync(SvidFactory.Create(SpiffeId));

        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
            await fixture.Client.FetchJWTSVIDAsync(
                new JWTSVIDRequest
                {
                    Audience = { "bella-api" },
                    SpiffeId = "spiffe://acme/payments/prod/other-service",
                },
                fixture.Headers, cancellationToken: Ct));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact(Timeout = 30_000)]
    public async Task FetchJWTBundles_serves_the_JWKS_keyed_by_trust_domain()
    {
        await using var fixture = await AgentFixture.StartAsync(SvidFactory.Create(SpiffeId));

        using var call = fixture.Client.FetchJWTBundles(
            new JWTBundlesRequest(), fixture.Headers, cancellationToken: Ct);
        Assert.True(await call.ResponseStream.MoveNext(Ct));

        var bundles = call.ResponseStream.Current.Bundles;
        Assert.True(bundles.ContainsKey(TrustDomain));

        var jwks = bundles[TrustDomain].ToStringUtf8();
        Assert.Contains("\"keys\"", jwks, StringComparison.Ordinal);
    }

    [Fact(Timeout = 30_000)]
    public async Task ValidateJWTSVID_reports_UNIMPLEMENTED_rather_than_guessing()
    {
        // The worst possible stub in this service would be one that answered "valid". Unimplemented is
        // what the gRPC contract has for exactly this, and the message says where to validate instead.
        await using var fixture = await AgentFixture.StartAsync(SvidFactory.Create(SpiffeId));

        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
            await fixture.Client.ValidateJWTSVIDAsync(
                new ValidateJWTSVIDRequest { Audience = "bella-api", Svid = "not.a.token" },
                fixture.Headers, cancellationToken: Ct));

        Assert.Equal(StatusCode.Unimplemented, ex.StatusCode);
        Assert.Contains("FetchJWTBundles", ex.Status.Detail, StringComparison.Ordinal);
    }

    /// <summary>Whether the certificate's SAN extension carries this SPIFFE ID as a URI.</summary>
    /// <remarks>
    /// A byte-level check rather than a parsed one: the BCL's
    /// <c>X509SubjectAlternativeNameExtension</c> enumerates DNS names and IP addresses but has NO URI
    /// accessor, which is why the API suite hand-rolls an ASN.1 reader for the same job. Here the
    /// question is narrower — "is this exact identity in the extension" — and a URI SAN is an
    /// IA5String, i.e. ASCII, so its bytes appear verbatim inside the extension's DER.
    ///
    /// <para>Deliberately checked against the DELIVERED certificate rather than the one the fixture
    /// built, so a truncated or re-encoded chain is caught here rather than by a peer later.</para>
    /// </remarks>
    private static bool CarriesSpiffeUriSan(X509Certificate2 cert, string spiffeId)
    {
        var extension = cert.Extensions["2.5.29.17"];
        if (extension is null)
        {
            return false;
        }

        var needle = Encoding.ASCII.GetBytes(spiffeId);
        return extension.RawData.AsSpan().IndexOf(needle) >= 0;
    }

    // ===== fixture =====

    /// <summary>Runs a real <see cref="WorkloadApiServer"/> on a temp socket with a real gRPC client.</summary>
    private sealed class AgentFixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _serverTask;
        private readonly GrpcChannel _channel;
        private readonly string _directory;

        internal SvidAgent Agent { get; }

        internal SpiffeWorkloadAPI.SpiffeWorkloadAPIClient Client { get; }

        /// <summary>The metadata the SPIFFE spec requires on every call.</summary>
        internal Metadata Headers { get; } = new() { { WorkloadApiService.SecurityHeader, "true" } };

        private AgentFixture(
            SvidAgent agent, Task serverTask, GrpcChannel channel, string directory, CancellationTokenSource cts)
        {
            Agent = agent;
            _serverTask = serverTask;
            _channel = channel;
            _directory = directory;
            _cts = cts;
            Client = new SpiffeWorkloadAPI.SpiffeWorkloadAPIClient(channel);
        }

        internal static Task<AgentFixture> StartAsync(AttestedSvid svid, bool attestOnStart = true) =>
            StartAsync(new ControllableSvidSource(svid), attestOnStart);

        internal static async Task<AgentFixture> StartAsync(
            ControllableSvidSource source, bool attestOnStart = true)
        {
            var agent = new SvidAgent(source);
            if (attestOnStart)
            {
                await agent.EnsureFreshAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
            }

            // A short path: a Unix socket address is capped near 104 bytes on macOS, and the temp
            // directory plus a GUID can exceed it — producing a bind error that reads like a permission
            // problem.
            var directory = Path.Combine(Path.GetTempPath(), $"bws{Guid.NewGuid().ToString("N")[..6]}");
            var socket = Path.Combine(directory, "w.sock");

            // The directory is NOT pre-created here — WorkloadApiServer's PrepareDirectory creates it
            // with 0700. Creating it first with the default 0755 makes the server REFUSE to start, which
            // is the T022 guard working correctly (it will not widen or silently accept a directory
            // anyone can read, because the socket's permissions are the whole authorisation boundary).
            // That refusal is how the first run of this fixture failed, and leaving the directory to the
            // server is both simpler and a small proof that the production path creates it safely.

            var cts = new CancellationTokenSource();
            var server = new WorkloadApiServer(
                agent,
                new StubJwtSvidSource(),
                new SvidSocketLocation(socket, SvidSocketPathSource.Explicit));

            var serverTask = server.RunAsync(cts.Token);
            await WaitForSocketAsync(socket, serverTask, cts.Token);

            var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
            {
                HttpHandler = new SocketsHttpHandler
                {
                    // The dial itself is the interop surface: a real HTTP/2 connection over a real UDS,
                    // not an in-memory test transport that would skip Kestrel entirely.
                    ConnectCallback = async (_, ct) =>
                    {
                        var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                        await s.ConnectAsync(new UnixDomainSocketEndPoint(socket), ct);
                        return new NetworkStream(s, ownsSocket: true);
                    },
                },
            });

            return new AgentFixture(agent, serverTask, channel, directory, cts);
        }

        private static async Task WaitForSocketAsync(string path, Task serverTask, CancellationToken ct)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (DateTimeOffset.UtcNow < deadline)
            {
                // Observe the server FIRST. Waiting only on the socket turns any startup failure into a
                // 15-second timeout that says "never became connectable" and hides the actual cause —
                // which is how the first run of this fixture reported a bind problem it did not have.
                if (serverTask.IsCompleted)
                {
                    await serverTask;
                    throw new InvalidOperationException(
                        "The Workload API server stopped before the socket became connectable.");
                }

                if (File.Exists(path))
                {
                    try
                    {
                        using var probe = new Socket(
                            AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                        await probe.ConnectAsync(new UnixDomainSocketEndPoint(path), ct);
                        return;
                    }
                    catch (SocketException)
                    {
                        // Bound but not accepting yet.
                    }
                }

                await Task.Delay(50, ct);
            }

            throw new TimeoutException($"The Workload API socket at '{path}' never became connectable.");
        }

        public async ValueTask DisposeAsync()
        {
            _channel.Dispose();
            await _cts.CancelAsync();
            try
            {
                await _serverTask;
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }

            _cts.Dispose();

            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }
    }

    /// <summary>An SVID source whose next answer the test chooses.</summary>
    private sealed class ControllableSvidSource(AttestedSvid initial) : ISvidSource
    {
        internal AttestedSvid Next { get; set; } = initial;

        public Task<AttestedSvid> AttestAsync(CancellationToken ct) => Task.FromResult(Next);
    }

    /// <summary>Mints a recognisable fake token per audience, and a minimal JWKS.</summary>
    private sealed class StubJwtSvidSource : IJwtSvidSource
    {
        public Task<JwtSvid> IssueAsync(string audience, CancellationToken ct) =>
            Task.FromResult(new JwtSvid(
                // Not a real JWT: this test is about the Workload API's wire shape, and the audience is
                // embedded so a mixed-up per-audience response is visible.
                $"header.{audience}.signature",
                SpiffeId,
                DateTimeOffset.UtcNow.AddMinutes(5)));

        public Task<string> FetchJwtBundleAsync(CancellationToken ct) =>
            Task.FromResult("""{"keys":[{"kty":"RSA","kid":"k1","n":"AQAB","e":"AQAB"}]}""");
    }

    /// <summary>Real certificates — the DER assertions are meaningless against a stub.</summary>
    private static class SvidFactory
    {
        internal static AttestedSvid Create(string spiffeId, int minutes = 60)
        {
            using var caKey = RSA.Create(2048);
            var caRequest = new CertificateRequest(
                "CN=Acme SPIFFE CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            caRequest.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(true, false, 0, true));
            using var ca = caRequest.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));

            using var leafKey = RSA.Create(2048);
            var leafRequest = new CertificateRequest(
                "CN=workload", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddUri(new Uri(spiffeId));
            leafRequest.CertificateExtensions.Add(san.Build());
            leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));

            var serial = new byte[16];
            RandomNumberGenerator.Fill(serial);
            using var leaf = leafRequest.Create(
                ca, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(minutes), serial);

            var now = DateTimeOffset.UtcNow;
            return new AttestedSvid(
                Certificate: leaf.ExportCertificatePem(),
                PrivateKey: leafKey.ExportPkcs8PrivateKeyPem(),
                TrustBundle: ca.ExportCertificatePem(),
                SpiffeId: spiffeId,
                IssuedAt: now,
                ExpiresAt: now.AddMinutes(minutes));
        }
    }
}
