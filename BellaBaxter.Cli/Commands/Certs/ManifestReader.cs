using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace BellaCli.Commands.Certs;

// spec 020 (T021, US1) — reads the passphrase manifest that accompanies a certificate drop.
//
// The manifest is ADVISORY: it exists to catch an incomplete delivery, never to supply identity
// (that comes from the certificate itself) and never to gate an import. Its passphrase column is
// not needed to deploy anything — see specs/020-cert-bundle-import/research.md D11.
//
// Deliberately NO new dependency: the sheet is two columns, and this CLI ships as a published
// single-file global tool where a spreadsheet library would be several megabytes of cargo for a
// two-column read (research D2). An unrecognised sheet shape FAILS LOUDLY pointing at the csv
// fallback rather than guessing and mis-pairing passphrases.

/// <summary>One row of the manifest: a common name and its passphrase.</summary>
/// <remarks>
/// <see cref="Passphrase"/> is a secret. It must never be written to output, logs, or telemetry
/// in any mode (FR-013).
/// </remarks>
public sealed record ManifestRow(string CommonName, string Passphrase);

/// <summary>The manifest could not be understood. Carries operator-actionable guidance.</summary>
public sealed class ManifestFormatException(string message) : Exception(message);

public static class ManifestReader
{
    private const string SpreadsheetNamespace =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    /// <summary>Header spellings accepted for the common-name column.</summary>
    private static readonly string[] CommonNameHeaders = ["common name", "commonname", "cn", "nombre"];

    /// <summary>Header spellings accepted for the passphrase column, accents included or not.</summary>
    private static readonly string[] PassphraseHeaders =
    [
        "contraseña",
        "contrasena",
        "password",
        "passphrase",
        "clave",
    ];

    /// <summary>
    /// Reads a manifest from <c>.xlsx</c>, <c>.csv</c>, or <c>.tsv</c>.
    /// </summary>
    /// <exception cref="ManifestFormatException">The file's shape was not recognised.</exception>
    public static IReadOnlyList<ManifestRow> Read(string path)
    {
        if (!File.Exists(path))
        {
            throw new ManifestFormatException($"Manifest '{path}' does not exist.");
        }

        var rows = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".xlsx" => ReadSpreadsheet(path),
            ".csv" => ReadDelimited(path, ','),
            ".tsv" => ReadDelimited(path, '\t'),
            var other => throw new ManifestFormatException(
                $"Manifest format '{other}' is not supported. Supply .xlsx, .csv, or .tsv."
            ),
        };

        return Interpret(rows, path);
    }

    /// <summary>
    /// Turns raw cell grids into rows, locating the columns by header. A grid whose header is
    /// unrecognisable is refused with the csv escape hatch named — never silently mis-paired.
    /// </summary>
    private static IReadOnlyList<ManifestRow> Interpret(
        IReadOnlyList<IReadOnlyList<string>> grid,
        string path
    )
    {
        if (grid.Count == 0)
        {
            throw new ManifestFormatException($"Manifest '{Path.GetFileName(path)}' is empty.");
        }

        var header = grid[0];
        var nameColumn = FindColumn(header, CommonNameHeaders);
        var passphraseColumn = FindColumn(header, PassphraseHeaders);

        if (nameColumn < 0 || passphraseColumn < 0)
        {
            throw new ManifestFormatException(
                $"Could not find the expected columns in '{Path.GetFileName(path)}'. A manifest "
                    + "needs a 'Common Name' column and a 'Contraseña' column in its first row. "
                    + "If this sheet has an unusual layout, export it to CSV and pass that instead."
            );
        }

        var rows = new List<ManifestRow>();
        foreach (var line in grid.Skip(1))
        {
            if (nameColumn >= line.Count)
            {
                continue;
            }

            var commonName = line[nameColumn].Trim();
            if (string.IsNullOrEmpty(commonName))
            {
                continue;
            }

            var passphrase =
                passphraseColumn < line.Count ? line[passphraseColumn].Trim() : string.Empty;
            rows.Add(new ManifestRow(commonName, passphrase));
        }

        return rows;
    }

    private static int FindColumn(IReadOnlyList<string> header, string[] accepted)
    {
        for (var i = 0; i < header.Count; i++)
        {
            var cell = Normalize(header[i]);
            if (accepted.Any(a => cell == Normalize(a)))
            {
                return i;
            }
        }

        return -1;
    }

    private static string Normalize(string value) =>
        new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();

    // ── xlsx ─────────────────────────────────────────────────────────────────

    private static IReadOnlyList<IReadOnlyList<string>> ReadSpreadsheet(string path)
    {
        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(path);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            throw new ManifestFormatException(
                $"'{Path.GetFileName(path)}' could not be opened as a spreadsheet. "
                    + "If it is a legacy .xls file, save it as .xlsx or export it to CSV."
            );
        }

        using (archive)
        {
            var sheet =
                archive.GetEntry("xl/worksheets/sheet1.xml")
                ?? archive
                    .Entries.Where(e =>
                        e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal)
                        && e.FullName.EndsWith(".xml", StringComparison.Ordinal)
                    )
                    .OrderBy(e => e.FullName, StringComparer.Ordinal)
                    .FirstOrDefault();

            if (sheet is null)
            {
                throw new ManifestFormatException(
                    $"'{Path.GetFileName(path)}' contains no worksheet. Export it to CSV instead."
                );
            }

            var sharedStrings = ReadSharedStrings(archive);

            XDocument document;
            try
            {
                using var stream = sheet.Open();
                document = XDocument.Load(stream);
            }
            catch (System.Xml.XmlException)
            {
                throw new ManifestFormatException(
                    $"The worksheet inside '{Path.GetFileName(path)}' could not be read. "
                        + "Export it to CSV instead."
                );
            }

            var ns = XNamespace.Get(SpreadsheetNamespace);
            var grid = new List<IReadOnlyList<string>>();

            foreach (var row in document.Descendants(ns + "row"))
            {
                var cells = new List<string>();
                foreach (var cell in row.Elements(ns + "c"))
                {
                    var index = ColumnIndex(cell.Attribute("r")?.Value);
                    while (cells.Count < index)
                    {
                        cells.Add(string.Empty);
                    }

                    cells.Add(CellValue(cell, ns, sharedStrings));
                }

                grid.Add(cells);
            }

            return grid;
        }
    }

    private static List<string> ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null)
        {
            return [];
        }

        var ns = XNamespace.Get(SpreadsheetNamespace);
        using var stream = entry.Open();
        var document = XDocument.Load(stream);

        return document
            .Descendants(ns + "si")
            .Select(si => string.Concat(si.Descendants(ns + "t").Select(t => t.Value)))
            .ToList();
    }

    private static string CellValue(XElement cell, XNamespace ns, List<string> sharedStrings)
    {
        var type = cell.Attribute("t")?.Value;

        if (type == "inlineStr")
        {
            return string.Concat(cell.Descendants(ns + "t").Select(t => t.Value));
        }

        var value = cell.Element(ns + "v")?.Value ?? string.Empty;

        if (
            type == "s"
            && int.TryParse(value, CultureInfo.InvariantCulture, out var sharedIndex)
            && sharedIndex >= 0
            && sharedIndex < sharedStrings.Count
        )
        {
            return sharedStrings[sharedIndex];
        }

        return value;
    }

    /// <summary>Zero-based column index from a cell reference such as <c>B12</c>.</summary>
    private static int ColumnIndex(string? cellReference)
    {
        if (string.IsNullOrEmpty(cellReference))
        {
            return 0;
        }

        var index = 0;
        foreach (var character in cellReference)
        {
            if (!char.IsAsciiLetter(character))
            {
                break;
            }

            index = (index * 26) + (char.ToUpperInvariant(character) - 'A' + 1);
        }

        return Math.Max(0, index - 1);
    }

    // ── delimited text ───────────────────────────────────────────────────────

    private static IReadOnlyList<IReadOnlyList<string>> ReadDelimited(string path, char delimiter)
    {
        var grid = new List<IReadOnlyList<string>>();
        foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            grid.Add(SplitLine(line, delimiter));
        }

        return grid;
    }

    /// <summary>Splits one delimited line, honouring double-quoted fields.</summary>
    private static List<string> SplitLine(string line, char delimiter)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var character = line[i];

            if (quoted)
            {
                if (character == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    current.Append(character);
                }

                continue;
            }

            if (character == '"')
            {
                quoted = true;
            }
            else if (character == delimiter)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }
}
