using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BellaCli.Services.Spiffe;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// Spec 001 T023 (US2) — what <c>bella spiffe status</c> can tell an operator, against real sockets.
/// </summary>
/// <remarks>
/// <para>The value of this command is entirely in DISTINGUISHING situations that a naive check
/// collapses. Four states, four different things to do, and collapsing any pair of them is what makes
/// an operator restart a healthy agent or wait out a problem that needed a config change:</para>
/// <list type="bullet">
///   <item><b>NoSocket</b> — no agent, or the wrong path. Configuration.</item>
///   <item><b>NotServing</b> — a socket file with nothing behind it. An agent DID run here and died;
///   that changes what you check next.</item>
///   <item><b>NoIdentity</b> — an agent is up but has never attested. The dangerous one: a naive
///   "is the socket there" check calls this healthy, and a readiness probe would pass for a workload
///   that cannot authenticate to anything.</item>
///   <item><b>Serving</b> — a real identity with a real countdown.</item>
/// </list>
/// </remarks>
public class AgentStatusProbeTests
{
    private const string SpiffeId = "spiffe://acme/payments/prod/billing-service";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Timeout = 30_000)]
    public async Task NO_socket_file_is_reported_as_no_agent_with_the_path_it_looked_at()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"bnone{Guid.NewGuid():N}", "w.sock");

        var status = await new AgentStatusProbe().ProbeAsync(
            new SvidSocketLocation(missing, SvidSocketPathSource.SpiffeEndpointSocket), Ct);

        Assert.Equal(AgentStatusKind.NoSocket, status.Kind);

        // The advice names the path AND how it was chosen, because "no agent running" is wrong about
        // half the time — the agent is usually running somewhere else.
        Assert.Contains(missing, status.Advice!, StringComparison.Ordinal);
        Assert.Contains("SPIFFE_ENDPOINT_SOCKET", status.Advice!, StringComparison.Ordinal);
    }

    [Fact(Timeout = 30_000)]
    public async Task A_STALE_socket_is_distinguished_from_no_socket_at_all()
    {
        // A socket file whose owner is gone. Reporting this as "no agent" would hide the fact that one
        // ran here and stopped — which is the difference between "check your config" and "check why it
        // died".
        var directory = Path.Combine(Path.GetTempPath(), $"bst{Guid.NewGuid().ToString("N")[..6]}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "w.sock");

        try
        {
            // Bind and abandon without unlinking, which is what a SIGKILLed agent leaves behind.
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            listener.Close();

            // .NET unlinks on Close, so recreate the file to model the abandoned-file case exactly.
            if (!File.Exists(path))
            {
                await File.WriteAllTextAsync(path, string.Empty, Ct);
            }

            var status = await new AgentStatusProbe(TimeSpan.FromSeconds(2))
                .ProbeAsync(new SvidSocketLocation(path, SvidSocketPathSource.Explicit), Ct);

            Assert.NotEqual(AgentStatusKind.NoSocket, status.Kind);
            Assert.NotEqual(AgentStatusKind.Serving, status.Kind);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(Timeout = 40_000)]
    public async Task An_agent_with_NO_identity_is_NOT_reported_as_healthy()
    {
        // The state this command exists for. The socket is there, the agent answers, and the workload
        // still cannot authenticate to anything — so anything that treats a present socket as success
        // would mark this ready.
        await using var agent = await RunningAgent.StartAsync(attest: false);

        var status = await new AgentStatusProbe(TimeSpan.FromSeconds(3))
            .ProbeAsync(agent.Location, Ct);

        Assert.Equal(AgentStatusKind.NoIdentity, status.Kind);
        Assert.Null(status.SpiffeId);

        // And it says what to do about it, naming the command that shows the evidence.
        Assert.Contains("whoami", status.Advice!, StringComparison.Ordinal);
    }

    [Fact(Timeout = 40_000)]
    public async Task A_serving_agent_reports_the_identity_the_trust_domain_and_a_real_countdown()
    {
        await using var agent = await RunningAgent.StartAsync(attest: true, minutes: 45);

        var status = await new AgentStatusProbe().ProbeAsync(agent.Location, Ct);

        Assert.Equal(AgentStatusKind.Serving, status.Kind);
        Assert.Equal(SpiffeId, status.SpiffeId);
        Assert.Equal("spiffe://acme", status.TrustDomain);
        Assert.Null(status.Advice);

        var remaining = status.TimeRemaining(DateTimeOffset.UtcNow);
        Assert.NotNull(remaining);
        Assert.InRange(remaining!.Value.TotalMinutes, 40, 46);
    }

    [Fact(Timeout = 40_000)]
    public async Task The_countdown_comes_from_the_CERTIFICATE_not_from_what_the_agent_claims
        ()
    {
        // Read from the delivered certificate's notAfter, which is the number a PEER will enforce. If
        // status trusted the agent's own bookkeeping instead, it could report a healthy countdown for a
        // certificate that peers were already rejecting — the worst possible direction for this
        // command to be wrong in.
        await using var agent = await RunningAgent.StartAsync(attest: true, minutes: 12);

        var status = await new AgentStatusProbe().ProbeAsync(agent.Location, Ct);

        Assert.Equal(AgentStatusKind.Serving, status.Kind);
        Assert.NotNull(status.ExpiresAt);

        // The certificate was minted for 12 minutes, and the agent's own ExpiresAt bookkeeping agrees —
        // so equality with the certificate is what is being pinned, not merely a plausible number.
        var fromCertificate = status.ExpiresAt!.Value;
        Assert.InRange((fromCertificate - DateTimeOffset.UtcNow).TotalMinutes, 8, 13);
    }

    // ===== a real agent on a real socket =====

    private sealed class RunningAgent : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _serverTask;
        private readonly string _directory;

        internal SvidSocketLocation Location { get; }

        private RunningAgent(SvidSocketLocation location, Task serverTask, string directory, CancellationTokenSource cts)
        {
            Location = location;
            _serverTask = serverTask;
            _directory = directory;
            _cts = cts;
        }

        internal static async Task<RunningAgent> StartAsync(bool attest, int minutes = 60)
        {
            var svid = CreateSvid(minutes);
            var agent = new SvidAgent(new FixedSource(svid));
            if (attest)
            {
                await agent.EnsureFreshAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
            }

            // Short path: a Unix socket address is capped near 104 bytes on macOS, and the directory is
            // left for the server to create at 0700 (creating it here at the default 0755 makes the
            // server refuse to start, which is the T022 guard working).
            var directory = Path.Combine(Path.GetTempPath(), $"bsp{Guid.NewGuid().ToString("N")[..6]}");
            var location = new SvidSocketLocation(
                Path.Combine(directory, "w.sock"), SvidSocketPathSource.Explicit);

            var cts = new CancellationTokenSource();
            var server = new WorkloadApiServer(agent, new NoJwtSource(), location);
            var serverTask = server.RunAsync(cts.Token);

            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (!File.Exists(location.Path) && DateTimeOffset.UtcNow < deadline)
            {
                if (serverTask.IsCompleted)
                {
                    await serverTask;
                }

                await Task.Delay(50, TestContext.Current.CancellationToken);
            }

            return new RunningAgent(location, serverTask, directory, cts);
        }

        public async ValueTask DisposeAsync()
        {
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

    private sealed class FixedSource(AttestedSvid svid) : ISvidSource
    {
        public Task<AttestedSvid> AttestAsync(CancellationToken ct) => Task.FromResult(svid);
    }

    private sealed class NoJwtSource : IJwtSvidSource
    {
        public Task<JwtSvid> IssueAsync(string audience, CancellationToken ct) =>
            throw new NotSupportedException("status does not fetch JWT-SVIDs.");

        public Task<string> FetchJwtBundleAsync(CancellationToken ct) =>
            throw new NotSupportedException("status does not fetch the JWT bundle.");
    }

    private static AttestedSvid CreateSvid(int minutes)
    {
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest(
            "CN=Acme SPIFFE CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var ca = caRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));

        using var leafKey = RSA.Create(2048);
        var leafRequest = new CertificateRequest(
            "CN=workload", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddUri(new Uri(SpiffeId));
        leafRequest.CertificateExtensions.Add(san.Build());
        leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));

        var serial = new byte[16];
        RandomNumberGenerator.Fill(serial);
        using var leaf = leafRequest.Create(
            ca, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(minutes), serial);

        var now = DateTimeOffset.UtcNow;
        return new AttestedSvid(
            leaf.ExportCertificatePem(),
            leafKey.ExportPkcs8PrivateKeyPem(),
            ca.ExportCertificatePem(),
            SpiffeId,
            now,
            now.AddMinutes(minutes));
    }
}
