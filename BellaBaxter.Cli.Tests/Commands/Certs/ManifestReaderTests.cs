using System.IO.Compression;
using System.Text;
using BellaCli.Commands.Certs;

namespace BellaBaxter.Cli.Tests.Commands.Certs;

// spec 020 (T020, US1) — the manifest reader.
//
// The manifest is advisory, so the important behaviours are: read the real customer shape
// correctly, read the csv fallback identically, and REFUSE loudly rather than mis-pair
// passphrases when the sheet is not what we expect.

public class ManifestReaderTests : IDisposable
{
    private readonly List<string> _temporaryFiles = [];

    [Fact]
    public void Reads_an_xlsx_with_shared_strings()
    {
        var path = WriteXlsx(
            [
                ["Common Name", "Contraseña"],
                ["ADkushki.prosa.example", "ADkushkiPass@12"],
                ["adyen.prosa.example", "adyenPass@11"],
            ]
        );

        var rows = ManifestReader.Read(path);

        Assert.Equal(2, rows.Count);
        // Capitalisation is preserved exactly — the manifest's common name matches the
        // certificate's, and that is the join key.
        Assert.Equal("ADkushki.prosa.example", rows[0].CommonName);
        Assert.Equal("ADkushkiPass@12", rows[0].Passphrase);
        Assert.Equal("adyen.prosa.example", rows[1].CommonName);
    }

    [Fact]
    public void Reads_a_csv_identically()
    {
        var path = WriteText(
            "manifest.csv",
            "Common Name,Contraseña\nADkushki.prosa.example,ADkushkiPass@12\nadyen.prosa.example,adyenPass@11\n"
        );

        var rows = ManifestReader.Read(path);

        Assert.Equal(2, rows.Count);
        Assert.Equal("ADkushkiPass@12", rows[0].Passphrase);
    }

    [Fact]
    public void Reads_a_tsv()
    {
        var path = WriteText(
            "manifest.tsv",
            "Common Name\tContraseña\napp.example\tsecretpass\n"
        );

        var rows = ManifestReader.Read(path);

        Assert.Single(rows);
        Assert.Equal("app.example", rows[0].CommonName);
    }

    [Fact]
    public void Accepts_the_unaccented_header_spelling()
    {
        var path = WriteText("manifest.csv", "common name,contrasena\napp.example,pw\n");

        var rows = ManifestReader.Read(path);

        Assert.Single(rows);
    }

    [Fact]
    public void Honours_quoted_fields_containing_the_delimiter()
    {
        var path = WriteText(
            "manifest.csv",
            "Common Name,Contraseña\napp.example,\"pass,with,commas\"\n"
        );

        var rows = ManifestReader.Read(path);

        Assert.Equal("pass,with,commas", rows[0].Passphrase);
    }

    [Fact]
    public void Skips_blank_rows_rather_than_inventing_entries()
    {
        var path = WriteText(
            "manifest.csv",
            "Common Name,Contraseña\napp.example,pw\n,\nother.example,pw2\n"
        );

        var rows = ManifestReader.Read(path);

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void Tolerates_a_missing_passphrase_cell()
    {
        // The passphrase is not needed to deploy anything, so an absent one is not a failure.
        var path = WriteText("manifest.csv", "Common Name,Contraseña\napp.example\n");

        var rows = ManifestReader.Read(path);

        Assert.Single(rows);
        Assert.Equal(string.Empty, rows[0].Passphrase);
    }

    // ── Refusals ─────────────────────────────────────────────────────────────

    [Fact]
    public void An_unrecognised_header_is_refused_and_names_the_csv_fallback()
    {
        var path = WriteXlsx([["Nombre del sitio", "Llave secreta"], ["app.example", "pw"]]);

        var ex = Assert.Throws<ManifestFormatException>(() => ManifestReader.Read(path));

        // Refusing loudly is the point: guessing which column holds what would mis-pair
        // passphrases across 135 certificates.
        Assert.Contains("Common Name", ex.Message);
        Assert.Contains("CSV", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_empty_sheet_is_refused()
    {
        var path = WriteText("manifest.csv", "");

        Assert.Throws<ManifestFormatException>(() => ManifestReader.Read(path));
    }

    [Fact]
    public void A_missing_file_is_refused()
    {
        var ex = Assert.Throws<ManifestFormatException>(() =>
            ManifestReader.Read(Path.Combine(Path.GetTempPath(), "definitely-not-here.csv"))
        );

        Assert.Contains("does not exist", ex.Message);
    }

    [Fact]
    public void An_unsupported_extension_is_refused()
    {
        var path = WriteText("manifest.xls", "anything");

        var ex = Assert.Throws<ManifestFormatException>(() => ManifestReader.Read(path));

        Assert.Contains(".xlsx", ex.Message);
    }

    [Fact]
    public void A_file_that_is_not_really_a_spreadsheet_is_refused()
    {
        var path = WriteText("manifest.xlsx", "this is not a zip archive");

        var ex = Assert.Throws<ManifestFormatException>(() => ManifestReader.Read(path));

        Assert.Contains("spreadsheet", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Helpers: build a minimal but real .xlsx ──────────────────────────────

    private string WriteXlsx(string[][] rows)
    {
        var path = Path.Combine(Path.GetTempPath(), $"manifest-{Guid.NewGuid():N}.xlsx");
        _temporaryFiles.Add(path);

        var shared = new List<string>();
        foreach (var cell in rows.SelectMany(r => r))
        {
            if (!shared.Contains(cell))
            {
                shared.Add(cell);
            }
        }

        var sheet = new StringBuilder();
        sheet.Append(
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>"""
        );
        for (var r = 0; r < rows.Length; r++)
        {
            sheet.Append($"<row r=\"{r + 1}\">");
            for (var c = 0; c < rows[r].Length; c++)
            {
                var reference = $"{(char)('A' + c)}{r + 1}";
                sheet.Append(
                    $"<c r=\"{reference}\" t=\"s\"><v>{shared.IndexOf(rows[r][c])}</v></c>"
                );
            }

            sheet.Append("</row>");
        }

        sheet.Append("</sheetData></worksheet>");

        var strings = new StringBuilder();
        strings.Append(
            $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" count="{shared.Count}" uniqueCount="{shared.Count}">"""
        );
        foreach (var value in shared)
        {
            strings.Append($"<si><t>{System.Security.SecurityElement.Escape(value)}</t></si>");
        }

        strings.Append("</sst>");

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        Write(archive, "xl/worksheets/sheet1.xml", sheet.ToString());
        Write(archive, "xl/sharedStrings.xml", strings.ToString());
        return path;

        static void Write(ZipArchive archive, string name, string content)
        {
            using var entryStream = archive.CreateEntry(name).Open();
            using var writer = new StreamWriter(entryStream, Encoding.UTF8);
            writer.Write(content);
        }
    }

    private string WriteText(string fileName, string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-{fileName}");
        _temporaryFiles.Add(path);
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    public void Dispose()
    {
        foreach (var file in _temporaryFiles.Where(File.Exists))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // A leftover temp file must never fail a test.
            }
        }
    }
}
