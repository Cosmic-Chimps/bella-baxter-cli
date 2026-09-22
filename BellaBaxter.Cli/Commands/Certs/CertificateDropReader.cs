using BellaBaxter.Crypto.Certificates;

namespace BellaCli.Commands.Certs;

// spec 057 (T007) — what is left of spec 020's drop reader once the judging moved to
// CertificateDropAnalyzer: read the filesystem, hand the bytes over, decide nothing.
//
// The rules this used to hold (classify by content, one certificate and one key per folder, the
// per-file ceiling, identity from the leaf's common name) now live in the shared library so the
// console import reaches the same answers. Keeping a second copy here is what FR-008 forbids.

public static class CertificateDropReader
{
    /// <summary>
    /// Reads a drop: one immediate subdirectory per certificate. Unreadable files are passed over
    /// rather than failing the folder, which is what keeps a permission-denied stray harmless.
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
            entries.Add(
                CertificateDropAnalyzer.Analyze(Path.GetFileName(directory), ReadFiles(directory, ct))
            );
        }

        return new CertificateDrop(rootPath, entries);
    }

    private static List<DropFile> ReadFiles(string directory, CancellationToken ct)
    {
        var files = new List<DropFile>();

        foreach (var file in Directory.EnumerateFiles(directory))
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (new FileInfo(file).Length > CertificateDropAnalyzer.MaxFileBytes)
                {
                    continue;
                }

                files.Add(new DropFile(Path.GetFileName(file), File.ReadAllText(file)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
        }

        return files;
    }
}
