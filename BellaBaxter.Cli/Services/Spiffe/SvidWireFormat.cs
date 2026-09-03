using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace BellaCli.Services.Spiffe;

// Spec 001 T042 (US6) — the PEM-to-DER translation the SPIFFE Workload API requires.
//
// This is small and boring and it is the single most dangerous file in US6, because every mistake it
// can make is INVISIBLE at this end. Bella's HTTP surface speaks PEM; the Workload API spec is
// explicit that the wire carries DER:
//
//   x509_svid     — ASN.1 DER certificate chain, LEAF FIRST, intermediates allowed
//   x509_svid_key — ASN.1 DER PKCS#8 private key, unencrypted
//   bundle        — ASN.1 DER X.509 bundle for the trust domain
//
// Hand a standard client base64 PEM where DER is expected and it does not report a format error at the
// boundary: `go-spiffe` fails somewhere inside its own certificate parsing, or worse accepts a
// truncated read, and the operator sees a TLS handshake failure in a completely different process. So
// every conversion here is a named function with a test, rather than an inline `Convert.FromBase64String`
// at four call sites.
//
// ORDER IS PART OF THE CONTRACT for x509_svid: the leaf MUST come first. `X509Certificate2Collection.
// ImportFromPem` preserves the order it reads, and Bella emits leaf-first, so the natural reading is
// already correct — but it is asserted rather than assumed, because a chain silently reordered by some
// future helper would make peers reject the SVID with an unhelpful "unable to build chain".

/// <summary>Converts SVID material between Bella's PEM and the Workload API's DER.</summary>
public static class SvidWireFormat
{
    /// <summary>
    /// The certificate chain as one DER blob, leaf first — the <c>x509_svid</c> field.
    /// </summary>
    /// <remarks>
    /// Concatenated DER, which is how the spec expresses a chain in a single <c>bytes</c> field.
    /// </remarks>
    public static byte[] CertificateChainDer(string certificatePem)
    {
        var chain = ParseCertificates(certificatePem, "certificate chain");

        using var ms = new MemoryStream();
        foreach (var cert in chain)
        {
            var der = cert.RawData;
            ms.Write(der, 0, der.Length);
        }

        DisposeAll(chain);
        return ms.ToArray();
    }

    /// <summary>
    /// The private key as DER PKCS#8, unencrypted — the <c>x509_svid_key</c> field.
    /// </summary>
    /// <remarks>
    /// <para>Accepts either a PKCS#8 (<c>-----BEGIN PRIVATE KEY-----</c>) or a PKCS#1
    /// (<c>-----BEGIN RSA PRIVATE KEY-----</c>) / SEC1 (<c>EC PRIVATE KEY</c>) PEM, because which one
    /// arrives depends on the issuing CA, and re-exporting is the only way to be sure the wire carries
    /// PKCS#8 as the spec demands.</para>
    ///
    /// <para>An ENCRYPTED private key PEM is refused rather than passed through: the spec says the key
    /// MUST be unencrypted, and a client handed an encrypted blob cannot tell that from a corrupt one.</para>
    /// </remarks>
    public static byte[] PrivateKeyPkcs8Der(string privateKeyPem)
    {
        if (string.IsNullOrWhiteSpace(privateKeyPem))
        {
            throw new InvalidOperationException("The SVID has no private key to serve.");
        }

        if (privateKeyPem.Contains("ENCRYPTED PRIVATE KEY", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The SVID private key is encrypted. The SPIFFE Workload API requires an unencrypted "
                + "PKCS#8 key, and a client cannot distinguish an encrypted key from a corrupt one.");
        }

        // Try each algorithm rather than parsing the PEM header ourselves: ImportFromPem already knows
        // every label, and guessing from the label is how an EC key ends up rejected as malformed RSA.
        foreach (var import in new Func<byte[]>[]
                 {
                     () => ExportPkcs8(RSA.Create(), privateKeyPem),
                     () => ExportPkcs8(ECDsa.Create(), privateKeyPem),
                 })
        {
            try
            {
                return import();
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException)
            {
                // Wrong algorithm for this PEM — try the next.
            }
        }

        throw new InvalidOperationException(
            "The SVID private key could not be read as an RSA or ECDSA key. Bella issues both; if this "
            + "appears, the attestation response carried something else entirely.");
    }

    /// <summary>
    /// A trust bundle as concatenated DER — the <c>bundle</c> and <c>bundles</c> fields.
    /// </summary>
    /// <remarks>
    /// Every certificate in the PEM, not just the first. A bundle carrying two CAs is exactly what a
    /// rotation overlap looks like, and truncating it here would make peers reject SVIDs signed by the
    /// other one — the same defect the ASP.NET middleware had (spec 001 T040).
    /// </remarks>
    public static byte[] TrustBundleDer(string trustBundlePem)
    {
        var certs = ParseCertificates(trustBundlePem, "trust bundle");

        using var ms = new MemoryStream();
        foreach (var cert in certs)
        {
            ms.Write(cert.RawData, 0, cert.RawData.Length);
        }

        DisposeAll(certs);
        return ms.ToArray();
    }

    /// <summary>
    /// The trust-domain SPIFFE ID that keys the bundle maps, e.g. <c>spiffe://acme</c>.
    /// </summary>
    /// <remarks>
    /// The bundle maps in <c>X509BundlesResponse</c> and <c>JWTBundlesResponse</c> are keyed by trust
    /// domain, NOT by the workload's full SPIFFE ID. Keying by the full ID would produce a response a
    /// standard client parses happily and then finds no bundle in, because it looks up
    /// <c>spiffe://acme</c> and we wrote <c>spiffe://acme/payments/prod/billing-service</c>.
    /// </remarks>
    public static string TrustDomainId(string spiffeId)
    {
        if (string.IsNullOrWhiteSpace(spiffeId)
            || !spiffeId.StartsWith("spiffe://", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"'{spiffeId}' is not a SPIFFE ID, so its trust domain cannot be determined.");
        }

        var rest = spiffeId["spiffe://".Length..];
        var slash = rest.IndexOf('/');
        var trustDomain = slash < 0 ? rest : rest[..slash];

        if (trustDomain.Length == 0)
        {
            throw new InvalidOperationException(
                $"'{spiffeId}' has an empty trust domain.");
        }

        return $"spiffe://{trustDomain}";
    }

    private static byte[] ExportPkcs8(RSA key, string pem)
    {
        using (key)
        {
            key.ImportFromPem(pem);
            return key.ExportPkcs8PrivateKey();
        }
    }

    private static byte[] ExportPkcs8(ECDsa key, string pem)
    {
        using (key)
        {
            key.ImportFromPem(pem);
            return key.ExportPkcs8PrivateKey();
        }
    }

    private static List<X509Certificate2> ParseCertificates(string pem, string what)
    {
        if (string.IsNullOrWhiteSpace(pem))
        {
            throw new InvalidOperationException($"The SVID has no {what} to serve.");
        }

        var collection = new X509Certificate2Collection();
        try
        {
            collection.ImportFromPem(pem);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"The SVID's {what} could not be parsed: {ex.Message}", ex);
        }

        if (collection.Count == 0)
        {
            // Never serve an empty blob. A zero-length `bytes` field is a valid protobuf value, so a
            // client would receive a well-formed response containing nothing and report a parse error
            // of its own — which points at the client, not at us.
            throw new InvalidOperationException(
                $"The SVID's {what} contained no certificates.");
        }

        return [.. collection.Cast<X509Certificate2>()];
    }

    private static void DisposeAll(List<X509Certificate2> certs)
    {
        foreach (var cert in certs)
        {
            cert.Dispose();
        }
    }
}
