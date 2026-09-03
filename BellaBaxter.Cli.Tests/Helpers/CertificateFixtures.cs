using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace BellaBaxter.Cli.Tests.Helpers;

// spec 020 (T004) — CLI-side throwaway certificate material + a drop-directory builder.
//
// The CLI test project cannot reference the API test project, so this mirrors the shapes in
// BellaBaxter.Tests/Helpers/CertificateFixtures.cs. Everything is generated per test run:
// no real customer certificate, key, or passphrase belongs in this repository.

public static class CertificateFixtures
{
    public sealed record Bundle(string ChainPem, string PrivateKeyPem, string CommonName);

    /// <summary>Leaf-first chain (leaf → intermediate → self-signed root) + unencrypted PKCS#8 key.</summary>
    public static Bundle CreateValidChain(
        string commonName,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null
    )
    {
        var from = notBefore ?? DateTimeOffset.UtcNow.AddDays(-1);
        var to = notAfter ?? DateTimeOffset.UtcNow.AddDays(365);

        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest(
            "CN=Fixture Root CA",
            rootKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        rootRequest.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: true, false, 0, true)
        );
        using var root = rootRequest.CreateSelfSigned(from.AddDays(-10), to.AddDays(10));

        using var intermediateKey = RSA.Create(2048);
        var intermediateRequest = new CertificateRequest(
            "CN=Fixture Intermediate CA",
            intermediateKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        intermediateRequest.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: true, false, 0, true)
        );
        using var intermediatePublicOnly = intermediateRequest.Create(
            root,
            from.AddDays(-5),
            to.AddDays(5),
            RandomNumberGenerator.GetBytes(16)
        );
        // Create() returns the certificate WITHOUT its private key; re-attach so it can sign.
        using var intermediate = intermediatePublicOnly.CopyWithPrivateKey(intermediateKey);

        using var leafKey = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={commonName}, O=Fixture Org, C=MX",
            leafKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, false, 0, true)
        );
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(commonName);
        request.CertificateExtensions.Add(san.Build());

        var effectiveFrom = from < intermediate.NotBefore
            ? new DateTimeOffset(intermediate.NotBefore)
            : from;
        var effectiveTo = to > intermediate.NotAfter ? new DateTimeOffset(intermediate.NotAfter) : to;
        using var leaf = request.Create(
            intermediate,
            effectiveFrom,
            effectiveTo,
            RandomNumberGenerator.GetBytes(16)
        );

        var chainPem = string.Join(
            '\n',
            leaf.ExportCertificatePem(),
            intermediate.ExportCertificatePem(),
            root.ExportCertificatePem()
        );
        return new Bundle(chainPem, leafKey.ExportPkcs8PrivateKeyPem(), commonName);
    }

    public static Bundle CreateWithMismatchedKey(string commonName)
    {
        var bundle = CreateValidChain(commonName);
        using var stranger = RSA.Create(2048);
        return bundle with { PrivateKeyPem = stranger.ExportPkcs8PrivateKeyPem() };
    }

    public static Bundle CreateExpired(string commonName) =>
        CreateValidChain(
            commonName,
            DateTimeOffset.UtcNow.AddDays(-400),
            DateTimeOffset.UtcNow.AddDays(-30)
        );

    public static IReadOnlyList<string> SplitCertificateBlocks(string pem) =>
        Regex
            .Matches(
                pem,
                "-----BEGIN CERTIFICATE-----.*?-----END CERTIFICATE-----",
                RegexOptions.Singleline
            )
            .Select(m => m.Value)
            .ToList();
}

/// <summary>
/// Materialises a fake issuer drop in a temp directory — one subfolder per certificate, the
/// key in <c>key.pem</c> and the chain in a file whose name mangles the common name exactly
/// the way the customer's drop does (dots to underscores, lowercased).
/// </summary>
public sealed class DropBuilder : IDisposable
{
    private readonly List<(string CommonName, string Passphrase)> _manifest = [];

    public DropBuilder()
    {
        Root = Path.Combine(Path.GetTempPath(), "bella-drop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>Add a certificate as its own subfolder. The folder name need not match the CN.</summary>
    public DropBuilder Add(
        CertificateFixtures.Bundle bundle,
        string? folderName = null,
        string? passphrase = null,
        bool omitKey = false,
        bool omitChain = false
    )
    {
        var folder = Path.Combine(Root, folderName ?? ShortName(bundle.CommonName));
        Directory.CreateDirectory(folder);

        if (!omitKey)
        {
            File.WriteAllText(Path.Combine(folder, "key.pem"), bundle.PrivateKeyPem);
        }

        if (!omitChain)
        {
            var fileName = bundle.CommonName.Replace('.', '_').ToLowerInvariant() + ".pem";
            File.WriteAllText(Path.Combine(folder, fileName), bundle.ChainPem);
        }

        _manifest.Add((bundle.CommonName, passphrase ?? bundle.CommonName + "Pass@11"));
        return this;
    }

    /// <summary>Drop an arbitrary extra file into a certificate's folder (a second key, a note).</summary>
    public DropBuilder AddExtraFile(string folderName, string fileName, string content)
    {
        var folder = Path.Combine(Root, folderName);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, fileName), content);
        return this;
    }

    /// <summary>Write the manifest as delimited text next to the certificate folders.</summary>
    public string WriteCsvManifest(string fileName = "Passphrases.csv", char delimiter = ',')
    {
        var path = Path.Combine(Root, fileName);
        var lines = new List<string> { $"Common Name{delimiter}Contraseña" };
        lines.AddRange(_manifest.Select(r => $"{r.CommonName}{delimiter}{r.Passphrase}"));
        File.WriteAllLines(path, lines);
        return path;
    }

    /// <summary>The passphrase this builder recorded for a common name.</summary>
    public string PassphraseFor(string commonName) =>
        _manifest.First(r =>
            string.Equals(r.CommonName, commonName, StringComparison.OrdinalIgnoreCase)
        ).Passphrase;

    private static string ShortName(string commonName) => commonName.Split('.')[0];

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temp directory that refuses to delete must never fail a test.
        }
    }
}
