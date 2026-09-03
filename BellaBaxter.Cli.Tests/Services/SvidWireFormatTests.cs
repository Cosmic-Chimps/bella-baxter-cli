using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BellaCli.Services.Spiffe;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// Spec 001 T042 (US6) — the PEM-to-DER translation the SPIFFE Workload API requires.
/// </summary>
/// <remarks>
/// <para>Small, boring, and the most dangerous code in US6, because every mistake it can make is
/// invisible at this end. Bella's HTTP surface speaks PEM; the Workload API wire is DER. Hand a
/// standard client base64 PEM where DER belongs and it does not report a format error at the boundary
/// — <c>go-spiffe</c> fails inside its own certificate parsing, and the operator sees a TLS handshake
/// failure in a completely different process.</para>
/// </remarks>
public class SvidWireFormatTests
{
    [Fact]
    public void A_certificate_chain_converts_to_parseable_DER()
    {
        using var ca = Ca("Acme CA");
        using var leaf = Leaf(ca, "spiffe://acme/p/e/w");

        var der = SvidWireFormat.CertificateChainDer(leaf.ExportCertificatePem());

        // The assertion is that the BCL can load it as DER at all. PEM bytes would fail here.
        using var loaded = X509CertificateLoader.LoadCertificate(der);
        Assert.Equal(leaf.Thumbprint, loaded.Thumbprint);
    }

    [Fact]
    public void A_multi_certificate_chain_keeps_the_LEAF_FIRST()
    {
        // Order is part of the wire contract: the spec says the leaf MUST come first. A chain silently
        // reordered by some future helper would make peers reject the SVID with an unhelpful
        // "unable to build chain", pointing at the CA rather than at the encoding.
        using var ca = Ca("Acme CA");
        using var leaf = Leaf(ca, "spiffe://acme/p/e/w");

        var pem = leaf.ExportCertificatePem() + "\n" + ca.ExportCertificatePem();
        var der = SvidWireFormat.CertificateChainDer(pem);

        // Concatenated DER: the first certificate must be the leaf. LoadCertificate reads the first.
        using var first = X509CertificateLoader.LoadCertificate(der);
        Assert.Equal(leaf.Thumbprint, first.Thumbprint);

        // And nothing was dropped — the blob is longer than the leaf alone.
        Assert.True(der.Length > leaf.RawData.Length, "the intermediate was discarded");
    }

    [Fact]
    public void An_RSA_private_key_converts_to_PKCS8_DER_that_still_matches_its_certificate()
    {
        // A key that parses but belongs to a different certificate is the worst outcome here: it fails
        // as a TLS handshake error with no useful message anywhere in the system.
        using var ca = Ca("Acme CA");
        using var key = RSA.Create(2048);
        using var leaf = Leaf(ca, "spiffe://acme/p/e/w", key);

        var der = SvidWireFormat.PrivateKeyPkcs8Der(key.ExportPkcs8PrivateKeyPem());

        using var reloaded = RSA.Create();
        reloaded.ImportPkcs8PrivateKey(der, out var consumed);
        Assert.Equal(der.Length, consumed);

        var payload = System.Text.Encoding.UTF8.GetBytes("pair-check");
        var signature = reloaded.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var pub = leaf.GetRSAPublicKey()!;
        Assert.True(pub.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void A_PKCS1_RSA_key_is_RE_EXPORTED_as_PKCS8()
    {
        // Which PEM label arrives depends on the issuing CA, and the spec requires PKCS#8 on the wire.
        // Passing a PKCS#1 body through unchanged would produce bytes a client cannot read.
        using var key = RSA.Create(2048);
        var pkcs1Pem = key.ExportRSAPrivateKeyPem();
        Assert.Contains("BEGIN RSA PRIVATE KEY", pkcs1Pem, StringComparison.Ordinal);

        var der = SvidWireFormat.PrivateKeyPkcs8Der(pkcs1Pem);

        using var reloaded = RSA.Create();
        reloaded.ImportPkcs8PrivateKey(der, out _);
        Assert.Equal(key.ExportPkcs8PrivateKey(), der);
    }

    [Fact]
    public void An_EC_key_is_handled_not_rejected_as_malformed_RSA()
    {
        // Guards the try-each-algorithm approach. Guessing the algorithm from the PEM label is how an
        // EC key ends up refused as a broken RSA key.
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var der = SvidWireFormat.PrivateKeyPkcs8Der(ec.ExportPkcs8PrivateKeyPem());

        using var reloaded = ECDsa.Create();
        reloaded.ImportPkcs8PrivateKey(der, out _);
        Assert.Equal(256, reloaded.KeySize);
    }

    [Fact]
    public void An_ENCRYPTED_private_key_is_refused_with_a_reason()
    {
        // The spec says the key MUST be unencrypted, and a client handed an encrypted blob cannot tell
        // that from a corrupt one — so refusing here is the only place the truth is available.
        using var key = RSA.Create(2048);
        var encrypted = key.ExportEncryptedPkcs8PrivateKeyPem(
            "passphrase", new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 1000));

        var ex = Assert.Throws<InvalidOperationException>(
            () => SvidWireFormat.PrivateKeyPkcs8Der(encrypted));

        Assert.Contains("encrypted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_trust_bundle_with_TWO_CAs_keeps_both()
    {
        // A two-CA bundle is what a rotation overlap looks like. Truncating it here would make peers
        // reject SVIDs signed by the other CA — the same defect the ASP.NET middleware had (T040).
        using var oldCa = Ca("Acme CA (outgoing)");
        using var newCa = Ca("Acme CA (incoming)");

        var der = SvidWireFormat.TrustBundleDer(
            oldCa.ExportCertificatePem() + "\n" + newCa.ExportCertificatePem());

        Assert.Equal(oldCa.RawData.Length + newCa.RawData.Length, der.Length);
    }

    [Fact]
    public void An_EMPTY_or_unparseable_input_THROWS_rather_than_producing_empty_bytes()
    {
        // A zero-length `bytes` field is a valid protobuf value, so serving one gives the client a
        // well-formed response containing nothing — and it reports a parse error of its own, which
        // points at the client rather than at us.
        Assert.Throws<InvalidOperationException>(() => SvidWireFormat.CertificateChainDer(""));
        Assert.Throws<InvalidOperationException>(() => SvidWireFormat.TrustBundleDer("   "));
        Assert.Throws<InvalidOperationException>(() => SvidWireFormat.PrivateKeyPkcs8Der(""));
        Assert.Throws<InvalidOperationException>(() => SvidWireFormat.CertificateChainDer("not a pem"));
    }

    [Theory]
    [InlineData("spiffe://acme/payments/prod/billing-service", "spiffe://acme")]
    [InlineData("spiffe://acme", "spiffe://acme")]
    [InlineData("spiffe://acme/", "spiffe://acme")]
    [InlineData("spiffe://tenant-with-dashes/p/e/w", "spiffe://tenant-with-dashes")]
    public void The_trust_domain_is_the_AUTHORITY_only(string spiffeId, string expected)
    {
        // The bundle maps are keyed by trust domain, not by the workload's full SPIFFE ID. Keying them
        // wrongly produces a response a standard client parses happily and then finds no bundle in,
        // because it looks up `spiffe://acme` and we wrote the whole path.
        Assert.Equal(expected, SvidWireFormat.TrustDomainId(spiffeId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://acme/p/e/w")]
    [InlineData("acme/p/e/w")]
    [InlineData("spiffe:///p/e/w")]
    public void A_non_SPIFFE_id_has_no_trust_domain_and_says_so(string notASpiffeId)
    {
        Assert.Throws<InvalidOperationException>(() => SvidWireFormat.TrustDomainId(notASpiffeId));
    }

    // ===== helpers =====

    private static X509Certificate2 Ca(string cn)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={cn}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));
    }

    private static X509Certificate2 Leaf(X509Certificate2 ca, string spiffeId, RSA? key = null)
    {
        var ownKey = key is null;
        var leafKey = key ?? RSA.Create(2048);
        try
        {
            var request = new CertificateRequest(
                "CN=workload", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddUri(new Uri(spiffeId));
            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));

            var serial = new byte[16];
            RandomNumberGenerator.Fill(serial);
            return request.Create(ca, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1), serial);
        }
        finally
        {
            if (ownKey)
            {
                leafKey.Dispose();
            }
        }
    }
}
