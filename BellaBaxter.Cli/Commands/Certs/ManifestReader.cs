using BellaBaxter.Crypto.Certificates;

namespace BellaCli.Commands.Certs;

// spec 057 (T008) — what is left of spec 020's manifest reader once the parsing moved to
// CertificateManifestReader: find the file, hand over its bytes.
//
// The pairing rule is the judgement, and it now lives in the shared library so the console import
// pairs passphrases the same way. This half only knows about paths, which the console does not have.

public static class ManifestReader
{
    /// <summary>
    /// Reads a manifest from disk. Supported shapes and every refusal message come from
    /// <see cref="CertificateManifestReader"/>; only "it is not there" is decided here.
    /// </summary>
    /// <exception cref="ManifestFormatException">The file is missing, or its shape was not recognised.</exception>
    public static IReadOnlyList<ManifestRow> Read(string path)
    {
        if (!File.Exists(path))
        {
            throw new ManifestFormatException($"Manifest '{path}' does not exist.");
        }

        return CertificateManifestReader.Read(path, File.ReadAllBytes(path));
    }
}
