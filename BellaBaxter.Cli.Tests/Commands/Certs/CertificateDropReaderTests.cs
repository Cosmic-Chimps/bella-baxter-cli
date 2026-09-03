using BellaBaxter.Cli.Tests.Helpers;
using BellaCli.Commands.Certs;

namespace BellaBaxter.Cli.Tests.Commands.Certs;

// spec 020 (T022, US1) — drop discovery.
//
// The two rules under test: files are classified by PEM CONTENT (not by name), and identity comes
// from the leaf certificate (not from the folder or file name). The customer's real drop breaks
// name-based assumptions in both directions, so these are correctness tests, not edge cases.

public class CertificateDropReaderTests
{
    [Fact]
    public void Finds_one_certificate_per_subfolder()
    {
        using var drop = new DropBuilder();
        drop.Add(CertificateFixtures.CreateValidChain("adyen.prosa.example"))
            .Add(CertificateFixtures.CreateValidChain("albatross.prosa.example"));

        var result = CertificateDropReader.Read(drop.Root);

        Assert.Equal(2, result.Entries.Count);
        Assert.All(result.Entries, e => Assert.True(e.HasMaterial));
    }

    [Fact]
    public void Identity_comes_from_the_certificate_not_the_folder_name()
    {
        // The real drop: folder "ADkushki", file "adkushki_prosa_com_mx.pem", common name
        // "ADkushki.prosa.com.mx". Only the certificate preserves the capitalisation.
        using var drop = new DropBuilder();
        drop.Add(
            CertificateFixtures.CreateValidChain("ADkushki.prosa.example"),
            folderName: "ADkushki"
        );

        var entry = CertificateDropReader.Read(drop.Root).Entries.Single();

        Assert.Equal("ADkushki", entry.SourceDirectory);
        Assert.Equal("ADkushki.prosa.example", entry.CommonName);
    }

    [Fact]
    public void Classifies_files_by_content_not_by_name()
    {
        using var drop = new DropBuilder();
        var bundle = CertificateFixtures.CreateValidChain("app.example");
        Directory.CreateDirectory(Path.Combine(drop.Root, "oddly-named"));
        File.WriteAllText(
            Path.Combine(drop.Root, "oddly-named", "private-material.txt"),
            bundle.PrivateKeyPem
        );
        File.WriteAllText(
            Path.Combine(drop.Root, "oddly-named", "chain.dat"),
            bundle.ChainPem
        );

        var entry = CertificateDropReader.Read(drop.Root).Entries.Single();

        Assert.True(entry.HasMaterial);
        Assert.Equal("app.example", entry.CommonName);
    }

    [Fact]
    public void Reads_a_combined_file_holding_both_blocks()
    {
        using var drop = new DropBuilder();
        var bundle = CertificateFixtures.CreateValidChain("combined.example");
        Directory.CreateDirectory(Path.Combine(drop.Root, "combined"));
        File.WriteAllText(
            Path.Combine(drop.Root, "combined", "bundle.pem"),
            bundle.ChainPem + "\n" + bundle.PrivateKeyPem
        );

        var entry = CertificateDropReader.Read(drop.Root).Entries.Single();

        Assert.True(entry.HasMaterial);
        Assert.Equal("combined.example", entry.CommonName);
    }

    [Fact]
    public void Ignores_incidental_non_certificate_files()
    {
        using var drop = new DropBuilder();
        drop.Add(CertificateFixtures.CreateValidChain("app.example"), folderName: "app")
            .AddExtraFile("app", ".DS_Store", "\0\0binary junk")
            .AddExtraFile("app", "notes.txt", "renewed by the other department");

        var entry = CertificateDropReader.Read(drop.Root).Entries.Single();

        Assert.True(entry.HasMaterial);
    }

    // ── Skips (reported, never silent, never fatal) ──────────────────────────

    [Fact]
    public void A_folder_with_no_certificate_is_skipped_with_a_reason()
    {
        using var drop = new DropBuilder();
        drop.Add(CertificateFixtures.CreateValidChain("app.example"), omitChain: true);

        var entry = CertificateDropReader.Read(drop.Root).Entries.Single();

        Assert.False(entry.HasMaterial);
        Assert.Equal(DropEntrySkipReason.NoCertificate, entry.SkipReason);
        Assert.False(string.IsNullOrWhiteSpace(entry.SkipDetail));
    }

    [Fact]
    public void A_folder_with_no_private_key_is_skipped_with_a_reason()
    {
        using var drop = new DropBuilder();
        drop.Add(CertificateFixtures.CreateValidChain("app.example"), omitKey: true);

        var entry = CertificateDropReader.Read(drop.Root).Entries.Single();

        Assert.False(entry.HasMaterial);
        Assert.Equal(DropEntrySkipReason.NoPrivateKey, entry.SkipReason);
    }

    [Fact]
    public void A_folder_with_two_keys_is_skipped_rather_than_guessed()
    {
        using var drop = new DropBuilder();
        var other = CertificateFixtures.CreateValidChain("other.example");
        drop.Add(CertificateFixtures.CreateValidChain("app.example"), folderName: "app")
            .AddExtraFile("app", "key-2.pem", other.PrivateKeyPem);

        var entry = CertificateDropReader.Read(drop.Root).Entries.Single();

        Assert.Equal(DropEntrySkipReason.MultiplePrivateKeys, entry.SkipReason);
    }

    [Fact]
    public void A_folder_with_two_certificates_is_skipped_rather_than_guessed()
    {
        using var drop = new DropBuilder();
        var other = CertificateFixtures.CreateValidChain("other.example");
        drop.Add(CertificateFixtures.CreateValidChain("app.example"), folderName: "app")
            .AddExtraFile("app", "second-chain.pem", other.ChainPem);

        var entry = CertificateDropReader.Read(drop.Root).Entries.Single();

        Assert.Equal(DropEntrySkipReason.MultipleCertificates, entry.SkipReason);
    }

    [Fact]
    public void An_empty_drop_yields_no_entries()
    {
        using var drop = new DropBuilder();

        var result = CertificateDropReader.Read(drop.Root);

        Assert.Empty(result.Entries);
    }

    [Fact]
    public void A_missing_directory_is_an_error()
    {
        Assert.Throws<DirectoryNotFoundException>(() =>
            CertificateDropReader.Read(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()))
        );
    }

    [Fact]
    public void Only_immediate_subdirectories_are_candidates()
    {
        // A drop delivered one level deeper must not be silently half-read: the wrapper folder
        // yields nothing, which is what makes the "nested one level deeper" hint honest.
        using var drop = new DropBuilder();
        var nested = Path.Combine(drop.Root, "wrapper", "app");
        Directory.CreateDirectory(nested);
        var bundle = CertificateFixtures.CreateValidChain("app.example");
        File.WriteAllText(Path.Combine(nested, "key.pem"), bundle.PrivateKeyPem);
        File.WriteAllText(Path.Combine(nested, "app.pem"), bundle.ChainPem);

        var result = CertificateDropReader.Read(drop.Root);

        var entry = Assert.Single(result.Entries);
        Assert.Equal("wrapper", entry.SourceDirectory);
        Assert.False(entry.HasMaterial);
    }
}
