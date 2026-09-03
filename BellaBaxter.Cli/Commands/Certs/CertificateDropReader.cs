using BellaBaxter.Crypto.Certificates;

namespace BellaCli.Commands.Certs;

// spec 020 (T023, US1) — discovers the certificates in an issuer drop.
//
// Two rules drive everything here:
//
//  1. Files are classified by their PEM CONTENT, never by name. The customer's drop happens to
//     use key.pem plus <cn_with_underscores>.pem, but a drop whose files are named differently
//     must still import (FR-003).
//  2. Identity comes from the leaf certificate's common name, never from the folder or file name
//     (FR-002). In the real drop the folder is "ADkushki", the file is "adkushki_...", and the
//     common name is "ADkushki.prosa.com.mx" — only the certificate is trustworthy.

/// <summary>Why a subdirectory produced no certificate.</summary>
public enum DropEntrySkipReason
{
    None = 0,
    NoCertificate,
    NoPrivateKey,
    MultipleCertificates,
    MultiplePrivateKeys,
    UnreadableCertificate,
}

/// <summary>One subdirectory of a drop, and what was found in it.</summary>
public sealed record CertificateDropEntry
{
    /// <summary>Directory name — a human label, never an identity.</summary>
    public required string SourceDirectory { get; init; }

    public string? ChainPem { get; init; }
    public string? PrivateKeyPem { get; init; }

    /// <summary>The common name parsed from the leaf, when the material was readable.</summary>
    public string? CommonName { get; init; }

    public DropEntrySkipReason SkipReason { get; init; }
    public string? SkipDetail { get; init; }

    public bool HasMaterial =>
        SkipReason == DropEntrySkipReason.None
        && !string.IsNullOrEmpty(ChainPem)
        && !string.IsNullOrEmpty(PrivateKeyPem);
}

/// <summary>Everything a drop directory yielded.</summary>
public sealed record CertificateDrop(
    string RootPath,
    IReadOnlyList<CertificateDropEntry> Entries
);

public static class CertificateDropReader
{
    /// <summary>
    /// Reads a drop: one immediate subdirectory per certificate. Subdirectories that yield no
    /// certificate material are reported as skipped, not failed — that is what keeps incidental
    /// files and stray folders harmless.
    /// </summary>
    public static CertificateDrop Read(string rootPath, CancellationToken ct = default)
    {
        if (!Directory.Exists(rootPath))
        {
            throw new DirectoryNotFoundException($"Drop directory '{rootPath}' does not exist.");
        }

        var entries = new List<CertificateDropEntry>();

        foreach (
            var directory in Directory
                .EnumerateDirectories(rootPath)
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
        )
        {
            ct.ThrowIfCancellationRequested();
            entries.Add(ReadEntry(directory, ct));
        }

        return new CertificateDrop(rootPath, entries);
    }

    private static CertificateDropEntry ReadEntry(string directory, CancellationToken ct)
    {
        var name = Path.GetFileName(directory);
        var chains = new List<string>();
        var keys = new List<string>();

        foreach (var file in Directory.EnumerateFiles(directory))
        {
            ct.ThrowIfCancellationRequested();

            string content;
            try
            {
                // A drop should never contain anything large; refuse to slurp something absurd.
                if (new FileInfo(file).Length > 1024 * 1024)
                {
                    continue;
                }

                content = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var hasCertificate = CertificateBundleReader.SplitCertificateBlocks(content).Count > 0;
            var keyBlock = CertificateBundleReader.ExtractPrivateKeyBlock(content);

            if (hasCertificate)
            {
                chains.Add(content);
            }

            if (keyBlock is not null)
            {
                // A combined file holding both blocks counts once for each.
                keys.Add(hasCertificate ? keyBlock : content);
            }
        }

        if (chains.Count == 0)
        {
            return new CertificateDropEntry
            {
                SourceDirectory = name,
                SkipReason = DropEntrySkipReason.NoCertificate,
                SkipDetail = "no file in this folder contains a certificate.",
            };
        }

        if (chains.Count > 1)
        {
            return new CertificateDropEntry
            {
                SourceDirectory = name,
                SkipReason = DropEntrySkipReason.MultipleCertificates,
                SkipDetail =
                    $"{chains.Count} files contain certificates; a folder must hold exactly one.",
            };
        }

        if (keys.Count == 0)
        {
            return new CertificateDropEntry
            {
                SourceDirectory = name,
                ChainPem = chains[0],
                SkipReason = DropEntrySkipReason.NoPrivateKey,
                SkipDetail = "no file in this folder contains a private key.",
            };
        }

        if (keys.Count > 1)
        {
            return new CertificateDropEntry
            {
                SourceDirectory = name,
                ChainPem = chains[0],
                SkipReason = DropEntrySkipReason.MultiplePrivateKeys,
                SkipDetail =
                    $"{keys.Count} files contain private keys; a folder must hold exactly one.",
            };
        }

        var commonName = TryReadCommonName(chains[0]);
        if (commonName is null)
        {
            return new CertificateDropEntry
            {
                SourceDirectory = name,
                ChainPem = chains[0],
                PrivateKeyPem = keys[0],
                SkipReason = DropEntrySkipReason.UnreadableCertificate,
                SkipDetail = "the certificate's common name could not be read.",
            };
        }

        return new CertificateDropEntry
        {
            SourceDirectory = name,
            ChainPem = chains[0],
            PrivateKeyPem = keys[0],
            CommonName = commonName,
        };
    }

    /// <summary>The leaf's common name — the drop entry's only trustworthy identity.</summary>
    private static string? TryReadCommonName(string chainPem)
    {
        var blocks = CertificateBundleReader.SplitCertificateBlocks(chainPem);
        if (blocks.Count == 0)
        {
            return null;
        }

        try
        {
            using var leaf =
                System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(
                    blocks[0]
                );
            var commonName = leaf.GetNameInfo(
                System.Security.Cryptography.X509Certificates.X509NameType.SimpleName,
                forIssuer: false
            );
            return string.IsNullOrWhiteSpace(commonName) ? null : commonName;
        }
        catch (Exception ex)
            when (ex is System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            return null;
        }
    }
}
