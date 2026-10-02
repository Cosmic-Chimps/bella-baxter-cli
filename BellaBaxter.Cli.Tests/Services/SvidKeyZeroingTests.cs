using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BellaCli.Services.Spiffe;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// Backlog §2.31 / issue #710 — the agent clears the private key it holds when that key stops being
/// the one it serves.
/// </summary>
/// <remarks>
/// <para><b>How the clearing is observed.</b> <see cref="SvidPrivateKey.TakeOwnership"/> wraps the
/// caller's array without copying it, so a test that keeps its own reference to that array can read
/// it afterwards and see zeros. That is the seam; nothing here asserts anything about the garbage
/// collector, which would be both flaky and beside the point — the question is whether the agent
/// cleared ITS copy, not whether some other copy happened to be reclaimed.</para>
///
/// <para>What these tests cannot prove, and do not pretend to: that the attestation response's PEM
/// string, the protobuf copy handed to gRPC, or a client's own copy are cleared. They are not, and
/// cannot be; <see cref="SvidPrivateKey"/>'s file comment lists them.</para>
/// </remarks>
public class SvidKeyZeroingTests
{
    private static readonly DateTimeOffset IssuedAt = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    // 20% of 60 minutes remaining is T+48 (SvidAgentRotationTests pins the boundary).
    private static readonly DateTimeOffset AtRenewal = IssuedAt.AddMinutes(48);

    [Fact]
    public async Task A_rotation_clears_the_superseded_SVIDs_key()
    {
        var firstKey = RandomKeyBytes();
        var secondKey = RandomKeyBytes();
        var first = Svid(firstKey, IssuedAt);
        var second = Svid(secondKey, AtRenewal);
        var agent = new SvidAgent(new QueueSource(first, second));

        await agent.EnsureFreshAsync(IssuedAt, TestContext.Current.CancellationToken);
        Assert.False(AllZero(firstKey)); // held and current: untouched

        Assert.True(await agent.EnsureFreshAsync(AtRenewal, TestContext.Current.CancellationToken));

        Assert.True(AllZero(firstKey), "the superseded SVID's key buffer was not cleared");
        Assert.True(first.PrivateKey.IsDestroyed);
        Assert.False(AllZero(secondKey), "the CURRENT key must never be cleared by a rotation");
        Assert.False(second.PrivateKey.IsDestroyed);
    }

    [Fact]
    public async Task A_source_that_hands_back_the_SAME_SVID_does_not_clear_the_identity_now_current()
    {
        // A cached answer or a test stub can return the same SVID twice. Destroying "the previous" key
        // then clears the key that is being served — the agent would push a key of zeros.
        var key = RandomKeyBytes();
        var svid = Svid(key, IssuedAt);
        var agent = new SvidAgent(new QueueSource(svid, svid));

        await agent.EnsureFreshAsync(IssuedAt, TestContext.Current.CancellationToken);
        await agent.EnsureFreshAsync(AtRenewal, TestContext.Current.CancellationToken);

        Assert.False(AllZero(key));
        Assert.False(svid.PrivateKey.IsDestroyed);
    }

    [Fact]
    public async Task Disposing_the_agent_clears_the_key_it_holds()
    {
        var key = RandomKeyBytes();
        var agent = new SvidAgent(new QueueSource(Svid(key, IssuedAt)));
        await agent.EnsureFreshAsync(IssuedAt, TestContext.Current.CancellationToken);

        agent.Dispose();

        Assert.True(AllZero(key));
    }

    [Fact]
    public void A_destroyed_key_refuses_to_be_copied_instead_of_yielding_zeros()
    {
        var key = SvidPrivateKey.TakeOwnership(RandomKeyBytes());
        Assert.True(key.TryCopyToByteString(out var before));
        Assert.False(before.IsEmpty);

        key.Destroy();
        key.Destroy(); // idempotent

        Assert.False(key.TryCopyToByteString(out var after));
        Assert.True(after.IsEmpty);
    }

    [Fact]
    public void A_push_built_from_a_superseded_SVID_is_skipped_not_sent_with_a_cleared_key()
    {
        // The race this type exists for: a Workload API stream takes the previous SVID off its channel,
        // the rotation lands and clears its key, and only then does the stream build its response. It
        // must produce nothing (the replacement is already on the channel), never a key of zeros.
        var svid = RealSvid();
        Assert.NotNull(WorkloadApiService.BuildX509Response(svid));

        svid.PrivateKey.Destroy();

        Assert.Null(WorkloadApiService.BuildX509Response(svid));
    }

    [Fact]
    public void The_push_carries_the_held_PKCS8_bytes_exactly()
    {
        using var rsa = RSA.Create(2048);
        var der = rsa.ExportPkcs8PrivateKey();
        var svid = RealSvid(SvidPrivateKey.TakeOwnership((byte[])der.Clone()));

        var response = WorkloadApiService.BuildX509Response(svid);

        Assert.Equal(der, response!.Svids[0].X509SvidKey.ToByteArray());
    }

    [Fact]
    public void The_key_never_renders_its_material()
    {
        // AttestedSvid is a record, and a record's generated ToString prints every member. When the
        // key was a PEM string, logging an SVID printed the key.
        var svid = RealSvid();
        Assert.DoesNotContain("PRIVATE KEY", svid.ToString(), StringComparison.Ordinal);
        Assert.Equal("[private key]", svid.PrivateKey.ToString());
    }

    [Fact]
    public async Task Attestation_converts_the_PEM_once_into_the_PKCS8_bytes_the_agent_holds()
    {
        using var rsa = RSA.Create(2048);
        var source = SourceAnswering(rsa.ExportRSAPrivateKeyPem()); // PKCS#1, as some CAs issue

        var svid = await source.AttestAsync(TestContext.Current.CancellationToken);

        Assert.True(svid.PrivateKey.TryCopyToByteString(out var held));
        Assert.Equal(rsa.ExportPkcs8PrivateKey(), held.ToByteArray());
    }

    [Fact]
    public async Task An_unservable_key_fails_the_attestation_rather_than_every_later_push()
    {
        var source = SourceAnswering("-----BEGIN PRIVATE KEY-----bm90IGEga2V5-----END PRIVATE KEY-----");

        var ex = await Assert.ThrowsAsync<SvidAttestationException>(
            () => source.AttestAsync(TestContext.Current.CancellationToken));
        Assert.Contains("cannot serve", ex.Message, StringComparison.Ordinal);
    }

    // ===== helpers =====

    private static HttpSvidSource SourceAnswering(string privateKeyPem)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            certificate = "-----BEGIN CERTIFICATE-----x-----END CERTIFICATE-----",
            privateKey = privateKeyPem,
            trustBundle = "-----BEGIN CERTIFICATE-----y-----END CERTIFICATE-----",
            spiffeId = "spiffe://t/p/e/billing",
            expiresAt = DateTimeOffset.UtcNow.AddMinutes(60),
        });
        var http = new HttpClient(new FixedAnswer(body)) { BaseAddress = new Uri("https://bella.test") };
        return new HttpSvidSource(
            http,
            new SvidAttestationRequest(Guid.NewGuid(), "billing", "bax-token", "k8s", () => null));
    }

    private sealed class FixedAnswer(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
    }

    private static byte[] RandomKeyBytes() => RandomNumberGenerator.GetBytes(64);

    private static bool AllZero(byte[] bytes) => bytes.All(b => b == 0);

    private static AttestedSvid Svid(byte[] key, DateTimeOffset issued) =>
        new("cert", SvidPrivateKey.TakeOwnership(key), "ca", "spiffe://t/p/e/billing", issued, issued.AddMinutes(60));

    private static AttestedSvid RealSvid(SvidPrivateKey? key = null)
    {
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest(
            "CN=Acme SPIFFE CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var ca = caRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        using var leafKey = RSA.Create(2048);
        var leafRequest = new CertificateRequest(
            "CN=workload", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var serial = new byte[16];
        RandomNumberGenerator.Fill(serial);
        using var leaf = leafRequest.Create(
            ca, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(60), serial);

        var now = DateTimeOffset.UtcNow;
        return new AttestedSvid(
            leaf.ExportCertificatePem(),
            key ?? SvidPrivateKey.FromPem(leafKey.ExportPkcs8PrivateKeyPem()),
            ca.ExportCertificatePem(),
            "spiffe://t/p/e/billing",
            now,
            now.AddMinutes(60));
    }

    private sealed class QueueSource(params AttestedSvid[] queued) : ISvidSource
    {
        private int _index;

        public Task<AttestedSvid> AttestAsync(CancellationToken ct)
        {
            var svid = queued[Math.Min(_index, queued.Length - 1)];
            _index++;
            return Task.FromResult(svid);
        }
    }
}
