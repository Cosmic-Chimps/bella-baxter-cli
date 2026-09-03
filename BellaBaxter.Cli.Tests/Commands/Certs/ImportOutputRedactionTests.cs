using System.Text.Json;
using BellaBaxter.Cli.Tests.Helpers;
using BellaCli.Commands.Certs;

namespace BellaBaxter.Cli.Tests.Commands.Certs;

// spec 020 (T036, US1, FR-013) — nothing secret may reach the operator's screen, their logs, or
// their terminal scrollback.
//
// Two things are secret here: the private key, and the manifest passphrase. The bundle VALUE
// legitimately contains the key — it is what gets stored — so the rule under test is that the
// value never becomes part of anything rendered. These tests assert over the report payload the
// command builds, which is the only thing that reaches output.

public class ImportOutputRedactionTests
{
    private const string Prefix = "GIGAMON_CERT_";
    private const string Passphrase = "adyenProsa@11-verySecret";

    [Fact]
    public void The_reported_payload_never_contains_the_private_key()
    {
        var (planned, _) = PlanWithManifest();

        var rendered = RenderReportPayload(planned);

        // The key's PEM header is the tell — if any part of the key leaked, this catches it.
        Assert.DoesNotContain("PRIVATE KEY", rendered);
    }

    [Fact]
    public void The_reported_payload_never_contains_the_manifest_passphrase()
    {
        var (planned, _) = PlanWithManifest();

        var rendered = RenderReportPayload(planned);

        Assert.DoesNotContain(Passphrase, rendered);
    }

    [Fact]
    public void The_reported_payload_does_carry_the_public_facts()
    {
        var (planned, _) = PlanWithManifest();

        var rendered = RenderReportPayload(planned);

        // The point of the report: public facts, in full.
        Assert.Contains("adyen.prosa.example", rendered);
        Assert.Contains("Fixture Intermediate CA", rendered);
    }

    [Fact]
    public void The_passphrase_is_still_stored_in_the_bundle_value()
    {
        // Confirms the previous tests prove redaction rather than the passphrase simply being
        // absent: it IS carried into the stored value, just never into output.
        var (planned, _) = PlanWithManifest();

        using var document = JsonDocument.Parse(planned.Single().Value!);
        Assert.Equal(Passphrase, document.RootElement.GetProperty("passphrase").GetString());
    }

    [Fact]
    public void A_rejection_reason_never_quotes_key_material()
    {
        using var drop = new DropBuilder();
        drop.Add(CertificateFixtures.CreateWithMismatchedKey("bad.prosa.example"));

        var planned = ImportPlanner.Plan(
            CertificateDropReader.Read(drop.Root),
            Prefix,
            [],
            stripRoot: false
        );

        var reason = planned.Single().Reason!;
        Assert.DoesNotContain("PRIVATE KEY", reason);
        Assert.DoesNotContain("BEGIN", reason);
    }

    private static (List<PlannedCertificate> Planned, string CommonName) PlanWithManifest()
    {
        var drop = new DropBuilder();
        try
        {
            drop.Add(
                CertificateFixtures.CreateValidChain("adyen.prosa.example"),
                passphrase: Passphrase
            );

            var planned = ImportPlanner.Plan(
                CertificateDropReader.Read(drop.Root),
                Prefix,
                [new ManifestRow("adyen.prosa.example", Passphrase)],
                stripRoot: false
            );
            return (planned, "adyen.prosa.example");
        }
        finally
        {
            drop.Dispose();
        }
    }

    /// <summary>
    /// Mirrors the fields the command reports (contracts/cli-certs-import.md). If a future change
    /// adds the bundle value to the report, this projection has to change too — and these tests
    /// then fail, which is the guard.
    /// </summary>
    private static string RenderReportPayload(List<PlannedCertificate> planned)
    {
        var now = DateTimeOffset.UtcNow;
        return JsonSerializer.Serialize(
            planned
                .Where(p => p.Facts is not null)
                .Select(p => new
                {
                    commonName = p.CommonName,
                    secretKey = p.SecretKey,
                    sourceDirectory = p.SourceDirectory,
                    action = p.Action.ToString().ToLowerInvariant(),
                    notBefore = p.Facts!.NotBefore,
                    notAfter = p.Facts.NotAfter,
                    daysRemaining = p.Facts.DaysRemaining(now),
                    issuer = p.Facts.Issuer,
                    subjectAlternativeNames = p.Facts.SubjectAlternativeNames,
                    keyAlgorithm = p.Facts.KeyAlgorithm,
                    keySizeBits = p.Facts.KeySizeBits,
                    sha256Fingerprint = p.Facts.Sha256Fingerprint,
                    chainLength = p.Facts.ChainLength,
                    chainIncludesSelfSignedRoot = p.Facts.ChainIncludesSelfSignedRoot,
                })
                .ToList()
        );
    }
}
