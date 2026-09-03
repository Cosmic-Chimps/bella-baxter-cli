using BellaBaxter.Cli.Tests.Helpers;
using BellaCli.Commands.Certs;

namespace BellaBaxter.Cli.Tests.Commands.Certs;

// spec 020 (T037, US1) — the import end to end, from a materialised drop to a plan.
//
// Everything here runs against a real drop on disk through the real validator; only the API calls
// are absent (they are represented by the "existing secrets" dictionary the planner refines
// against). That covers the acceptance scenarios that matter: three certificates plan cleanly, a
// re-run reports unchanged, a corrupt folder is rejected while the rest still plan, and a
// duplicate common name is refused.

public class ImportCertsCommandTests
{
    private const string Prefix = "GIGAMON_CERT_";

    private static readonly Dictionary<string, string> Nothing = new(StringComparer.Ordinal);

    [Fact]
    public void A_three_certificate_drop_plans_three_creates()
    {
        using var drop = new DropBuilder();
        drop.Add(CertificateFixtures.CreateValidChain("adyen.prosa.example"))
            .Add(CertificateFixtures.CreateValidChain("albatross.prosa.example"))
            .Add(
                CertificateFixtures.CreateValidChain("ADkushki.prosa.example"),
                folderName: "ADkushki"
            );

        var planned = Plan(drop);

        Assert.Equal(3, planned.Count);
        Assert.All(planned, p => Assert.Equal(ImportAction.Created, p.Action));
        Assert.Contains(planned, p => p.SecretKey == Prefix + "adyen.prosa.example");
        // Capitalisation comes from the certificate, not the folder or file name.
        Assert.Contains(planned, p => p.SecretKey == Prefix + "ADkushki.prosa.example");
    }

    [Fact]
    public void Re_running_an_unchanged_drop_reports_everything_unchanged()
    {
        using var drop = new DropBuilder();
        drop.Add(CertificateFixtures.CreateValidChain("adyen.prosa.example"))
            .Add(CertificateFixtures.CreateValidChain("albatross.prosa.example"));

        var first = Plan(drop);
        // Simulate the environment after the first import.
        var stored = first.ToDictionary(p => p.SecretKey!, p => p.Value!, StringComparer.Ordinal);

        var second = Plan(drop);
        ImportPlanner.Refine(second, stored);

        Assert.All(second, p => Assert.Equal(ImportAction.Unchanged, p.Action));
        Assert.Equal(0, ImportPlanner.ExitCode(second, writeFailure: false));
    }

    [Fact]
    public void A_renewed_certificate_plans_an_update()
    {
        using var drop = new DropBuilder();
        drop.Add(CertificateFixtures.CreateValidChain("adyen.prosa.example"));
        var planned = Plan(drop);
        var stored = planned.ToDictionary(
            p => p.SecretKey!,
            p => p.Value!,
            StringComparer.Ordinal
        );

        // A new drop for the same common name: different key and certificate, same name.
        using var renewed = new DropBuilder();
        renewed.Add(CertificateFixtures.CreateValidChain("adyen.prosa.example"));
        var second = Plan(renewed);
        ImportPlanner.Refine(second, stored);

        Assert.Equal(ImportAction.Updated, second.Single().Action);
    }

    [Fact]
    public void One_bad_folder_is_rejected_while_the_rest_still_plan()
    {
        using var drop = new DropBuilder();
        drop.Add(CertificateFixtures.CreateValidChain("good-one.prosa.example"))
            .Add(CertificateFixtures.CreateWithMismatchedKey("bad-one.prosa.example"))
            .Add(CertificateFixtures.CreateValidChain("good-two.prosa.example"));

        var planned = Plan(drop);

        Assert.Equal(2, planned.Count(p => p.Action == ImportAction.Created));
        var rejected = Assert.Single(planned.Where(p => p.Action == ImportAction.Rejected));
        Assert.Contains("does not belong", rejected.Reason!);

        // Exit status alone tells the operator something needs attention (SC-009).
        Assert.Equal(2, ImportPlanner.ExitCode(planned, writeFailure: false));
    }

    [Fact]
    public void An_expired_certificate_is_rejected()
    {
        using var drop = new DropBuilder();
        drop.Add(CertificateFixtures.CreateExpired("expired.prosa.example"));

        var planned = Plan(drop);

        var rejected = Assert.Single(planned);
        Assert.Equal(ImportAction.Rejected, rejected.Action);
        Assert.Contains("expired", rejected.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Two_folders_with_the_same_common_name_are_refused()
    {
        using var drop = new DropBuilder();
        drop.Add(CertificateFixtures.CreateValidChain("duplicate.prosa.example"), folderName: "a")
            .Add(CertificateFixtures.CreateValidChain("duplicate.prosa.example"), folderName: "b");

        var planned = Plan(drop);

        Assert.Equal(1, planned.Count(p => p.Action == ImportAction.Created));
        var rejected = Assert.Single(planned.Where(p => p.Action == ImportAction.Rejected));
        Assert.Contains("same common name", rejected.Reason!);
    }

    [Fact]
    public void A_folder_missing_its_key_is_skipped_not_rejected()
    {
        using var drop = new DropBuilder();
        drop.Add(CertificateFixtures.CreateValidChain("keyless.prosa.example"), omitKey: true)
            .Add(CertificateFixtures.CreateValidChain("fine.prosa.example"));

        var planned = Plan(drop);

        Assert.Equal(1, planned.Count(p => p.Action == ImportAction.Skipped));
        Assert.Equal(1, planned.Count(p => p.Action == ImportAction.Created));
    }

    // ── --strip-root (FR-017) ────────────────────────────────────────────────

    [Fact]
    public void Strip_root_stores_a_shorter_chain_than_the_default()
    {
        using var drop = new DropBuilder();
        drop.Add(CertificateFixtures.CreateValidChain("app.prosa.example"));

        var asDelivered = Plan(drop, stripRoot: false).Single();
        var stripped = Plan(drop, stripRoot: true).Single();

        Assert.Equal(3, asDelivered.Facts!.ChainLength);
        Assert.True(asDelivered.Facts.ChainIncludesSelfSignedRoot);

        var deliveredBlocks = CountBlocks(asDelivered.Value!);
        var strippedBlocks = CountBlocks(stripped.Value!);
        Assert.Equal(3, deliveredBlocks);
        Assert.Equal(2, strippedBlocks);
    }

    [Fact]
    public void Strip_root_still_counts_as_a_change_against_a_stored_full_chain()
    {
        using var drop = new DropBuilder();
        drop.Add(CertificateFixtures.CreateValidChain("app.prosa.example"));
        var stored = Plan(drop, stripRoot: false)
            .ToDictionary(p => p.SecretKey!, p => p.Value!, StringComparer.Ordinal);

        var stripped = Plan(drop, stripRoot: true);
        ImportPlanner.Refine(stripped, stored);

        Assert.Equal(ImportAction.Updated, stripped.Single().Action);
    }

    // ── Manifest cross-check (FR-014) ────────────────────────────────────────

    [Fact]
    public void A_manifest_row_with_no_folder_is_a_warning_not_a_failure()
    {
        using var drop = new DropBuilder();
        drop.Add(CertificateFixtures.CreateValidChain("present.prosa.example"));

        var planned = Plan(drop);
        var manifest = new List<ManifestRow>
        {
            new("present.prosa.example", "pw1"),
            new("absent.prosa.example", "pw2"),
        };

        var warnings = ImportPlanner.CrossCheckManifest(manifest, planned);

        Assert.Contains(warnings, w => w.Contains("absent.prosa.example"));
        Assert.Equal(ImportAction.Created, planned.Single().Action);
        Assert.Equal(0, ImportPlanner.ExitCode(planned, writeFailure: false));
    }

    [Fact]
    public void A_folder_missing_from_the_manifest_is_a_warning()
    {
        using var drop = new DropBuilder();
        drop.Add(CertificateFixtures.CreateValidChain("unlisted.prosa.example"));

        var warnings = ImportPlanner.CrossCheckManifest(
            [new ManifestRow("something.else.example", "pw")],
            Plan(drop)
        );

        Assert.Contains(warnings, w => w.Contains("unlisted.prosa.example"));
    }

    [Fact]
    public void No_manifest_means_no_warnings()
    {
        using var drop = new DropBuilder();
        drop.Add(CertificateFixtures.CreateValidChain("app.prosa.example"));

        Assert.Empty(ImportPlanner.CrossCheckManifest([], Plan(drop)));
    }

    // ── Exit codes (contracts/cli-certs-import.md) ───────────────────────────

    [Fact]
    public void A_write_failure_outranks_a_clean_plan()
    {
        using var drop = new DropBuilder();
        drop.Add(CertificateFixtures.CreateValidChain("app.prosa.example"));

        Assert.Equal(3, ImportPlanner.ExitCode(Plan(drop), writeFailure: true));
    }

    private static List<PlannedCertificate> Plan(DropBuilder drop, bool stripRoot = false)
    {
        var read = CertificateDropReader.Read(drop.Root);
        return ImportPlanner.Plan(read, Prefix, [], stripRoot);
    }

    private static int CountBlocks(string bundleJson)
    {
        using var document = System.Text.Json.JsonDocument.Parse(bundleJson);
        var cert = document.RootElement.GetProperty("cert").GetString()!;
        return BellaBaxter.Crypto.Certificates.CertificateBundleReader
            .SplitCertificateBlocks(cert)
            .Count;
    }
}
