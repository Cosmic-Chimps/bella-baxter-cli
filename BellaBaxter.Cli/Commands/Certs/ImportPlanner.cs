using System.Text.Json;
using System.Text.RegularExpressions;
using BellaBaxter.Crypto.Certificates;

namespace BellaCli.Commands.Certs;

// spec 020 (US1) — the import's decisions, with no I/O.
//
// Everything that determines WHAT happens to a drop lives here: the provider gate, secret naming,
// validation outcomes, create/update/unchanged classification, the manifest cross-check, and the
// exit code. ImportCertsCommand does the talking to the API and the printing; this decides.
//
// The split exists so the fail-closed rules are testable without a network or a logged-in
// session — Constitution V wants the negative cases proven, and they cannot be proven through a
// class that needs a BellaClient.

/// <summary>What the import decided to do with one certificate.</summary>
public enum ImportAction
{
    Created,
    Updated,
    Unchanged,
    Rejected,
    Skipped,
}

/// <summary>One certificate's planned outcome. Carries no key material beyond the stored value.</summary>
public sealed record PlannedCertificate
{
    public required string SourceDirectory { get; init; }
    public required ImportAction Action { get; init; }
    public string? CommonName { get; init; }
    public string? SecretKey { get; init; }

    /// <summary>The serialised bundle to store. Contains key material — never print it.</summary>
    public string? Value { get; init; }

    public CertificateFacts? Facts { get; init; }
    public string? Reason { get; init; }

    public bool IsWrite => Action is ImportAction.Created or ImportAction.Updated;
}

public static partial class ImportPlanner
{
    /// <summary>The only provider type that may supply a certificate-source prefix.</summary>
    public const string CertificateSourceProviderType = "BellaBaxterSecretsSource";

    /// <summary>
    /// Secret keys for certificates carry a common name, so dots and hyphens are required. This
    /// is deliberately NOT the environment-variable pattern `bella secrets set` enforces, which
    /// would reject "adyen.prosa.com.mx" (research D8).
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-]*$")]
    private static partial Regex SecretKeyPattern();

    public static bool IsValidSecretKey(string key) => SecretKeyPattern().IsMatch(key);

    // ── The provider gate (FR-008) ───────────────────────────────────────────

    /// <summary>
    /// Decides whether a provider may be used as a certificate source. Refuses any other type,
    /// and refuses a source with no prefix configured — both BEFORE any certificate is read.
    /// </summary>
    public static string? RefuseSource(string slug, string? providerType, string? secretPrefix)
    {
        if (
            !string.Equals(
                providerType,
                CertificateSourceProviderType,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return $"Provider '{slug}' is a {providerType ?? "unknown"} provider. --provider must "
                + $"name a {CertificateSourceProviderType} provider, which is what supplies the "
                + "certificate naming prefix and what the rotation engine reads bundles from.";
        }

        if (string.IsNullOrWhiteSpace(secretPrefix))
        {
            return $"Provider '{slug}' has no 'secret_prefix' configured. Set it on the provider "
                + "before importing — the prefix is what binds these certificates to the rotation "
                + "that reads them.";
        }

        return null;
    }

    // ── Planning (FR-004, FR-005, FR-017) ────────────────────────────────────

    /// <summary>
    /// Validates every entry in the drop and produces the planned outcome for each. Nothing is
    /// written by this method — the whole drop is judged before the caller writes anything.
    /// </summary>
    public static List<PlannedCertificate> Plan(
        CertificateDrop drop,
        string secretPrefix,
        IReadOnlyList<ManifestRow> manifest,
        bool stripRoot,
        DateTimeOffset? now = null,
        CancellationToken ct = default
    )
    {
        var planned = new List<PlannedCertificate>();
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in drop.Entries)
        {
            ct.ThrowIfCancellationRequested();

            if (!entry.HasMaterial)
            {
                planned.Add(
                    new PlannedCertificate
                    {
                        SourceDirectory = entry.SourceDirectory,
                        Action = ImportAction.Skipped,
                        Reason = entry.SkipDetail ?? entry.SkipReason.ToString(),
                    }
                );
                continue;
            }

            var validation = CertificateBundleValidator.Validate(
                entry.ChainPem!,
                entry.PrivateKeyPem!,
                now
            );
            if (!validation.IsValid)
            {
                planned.Add(
                    new PlannedCertificate
                    {
                        SourceDirectory = entry.SourceDirectory,
                        CommonName = entry.CommonName,
                        Action = ImportAction.Rejected,
                        Reason = validation.Reason,
                    }
                );
                continue;
            }

            var facts = validation.Facts!;

            if (seen.TryGetValue(facts.CommonName, out var firstFolder))
            {
                planned.Add(
                    new PlannedCertificate
                    {
                        SourceDirectory = entry.SourceDirectory,
                        CommonName = facts.CommonName,
                        Action = ImportAction.Rejected,
                        Reason =
                            $"another folder ('{firstFolder}') in this drop carries the same common "
                            + "name; a drop must not contain two certificates for one name.",
                    }
                );
                continue;
            }

            seen[facts.CommonName] = entry.SourceDirectory;

            var secretKey = secretPrefix + facts.CommonName;
            if (!IsValidSecretKey(secretKey))
            {
                planned.Add(
                    new PlannedCertificate
                    {
                        SourceDirectory = entry.SourceDirectory,
                        CommonName = facts.CommonName,
                        Action = ImportAction.Rejected,
                        Reason =
                            $"the resulting secret name '{secretKey}' contains characters that are "
                            + "not allowed (letters, digits, dot, underscore and hyphen only).",
                    }
                );
                continue;
            }

            // Validate the chain AS DELIVERED; store what the operator asked for.
            var chainToStore = stripRoot
                ? CertificateBundleReader.StripSelfSignedRoot(entry.ChainPem!)
                : entry.ChainPem!;

            var passphrase =
                manifest
                    .FirstOrDefault(r =>
                        string.Equals(
                            r.CommonName,
                            facts.CommonName,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    ?.Passphrase ?? string.Empty;

            planned.Add(
                new PlannedCertificate
                {
                    SourceDirectory = entry.SourceDirectory,
                    CommonName = facts.CommonName,
                    SecretKey = secretKey,
                    Facts = facts,
                    Value = SerializeBundle(passphrase, entry.PrivateKeyPem!, chainToStore),
                    // Refined against existing state by Refine().
                    Action = ImportAction.Created,
                }
            );
        }

        return planned;
    }

    /// <summary>
    /// Refines Created into Updated or Unchanged using the environment's current values, so a
    /// re-run of an unchanged drop writes nothing (FR-010, FR-011).
    /// </summary>
    public static void Refine(
        List<PlannedCertificate> planned,
        IReadOnlyDictionary<string, string> existing
    )
    {
        for (var i = 0; i < planned.Count; i++)
        {
            var item = planned[i];
            if (item.SecretKey is null || item.Value is null || item.Action != ImportAction.Created)
            {
                continue;
            }

            if (!existing.TryGetValue(item.SecretKey, out var current))
            {
                continue;
            }

            planned[i] = item with
            {
                Action = BundlesMatch(current, item.Value)
                    ? ImportAction.Unchanged
                    : ImportAction.Updated,
            };
        }
    }

    // ── Bundle serialisation and comparison ──────────────────────────────────

    /// <summary>
    /// The frozen stored shape read by <c>CertSecretJson</c> in the rotation engine. Field names
    /// and set are part of the contract; do not add to them.
    /// </summary>
    public static string SerializeBundle(string passphrase, string keyPem, string chainPem) =>
        JsonSerializer.Serialize(
            new
            {
                passphrase,
                key = keyPem.Trim(),
                cert = chainPem.Trim(),
            }
        );

    /// <summary>
    /// Compares a stored bundle against a freshly built one on the three fields that matter, so
    /// property order and line endings cannot masquerade as a change.
    /// </summary>
    public static bool BundlesMatch(string storedValue, string candidateValue)
    {
        var stored = ParseBundle(storedValue);
        var candidate = ParseBundle(candidateValue);
        if (stored is null || candidate is null)
        {
            return false;
        }

        return NormalizePem(stored.Value.Key) == NormalizePem(candidate.Value.Key)
            && NormalizePem(stored.Value.Cert) == NormalizePem(candidate.Value.Cert)
            && stored.Value.Passphrase == candidate.Value.Passphrase;
    }

    private static (string Passphrase, string Key, string Cert)? ParseBundle(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return (Read(root, "passphrase"), Read(root, "key"), Read(root, "cert"));
        }
        catch (JsonException)
        {
            return null;
        }

        static string Read(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value)
                ? value.GetString() ?? string.Empty
                : string.Empty;
    }

    private static string NormalizePem(string pem) =>
        pem.Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    // ── Manifest cross-check (FR-014) ────────────────────────────────────────

    /// <summary>
    /// Compares the manifest against what the drop actually contained. Mismatches are warnings:
    /// the manifest exists to catch an incomplete delivery, not to gate the import.
    /// </summary>
    public static List<string> CrossCheckManifest(
        IReadOnlyList<ManifestRow> manifest,
        IReadOnlyList<PlannedCertificate> planned
    )
    {
        var warnings = new List<string>();
        if (manifest.Count == 0)
        {
            return warnings;
        }

        var found = planned
            .Where(p => p.CommonName is not null)
            .Select(p => p.CommonName!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var row in manifest.Where(r => !found.Contains(r.CommonName)))
        {
            warnings.Add(
                $"the manifest lists '{row.CommonName}' but the drop has no folder for it "
                    + "(the delivery may be incomplete)."
            );
        }

        var listed = manifest.Select(r => r.CommonName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in found.Where(n => !listed.Contains(n)))
        {
            warnings.Add($"'{name}' is in the drop but not listed in the manifest.");
        }

        return warnings;
    }

    // ── Exit code (FR-005, SC-009) ───────────────────────────────────────────

    /// <summary>
    /// 0 = everything stored or already current · 2 = something was rejected · 3 = a write failed
    /// partway. 1 is reserved for the caller's usage/auth/gate refusals, where nothing was tried.
    /// </summary>
    public static int ExitCode(IReadOnlyList<PlannedCertificate> planned, bool writeFailure)
    {
        if (writeFailure)
        {
            return 3;
        }

        return planned.Any(p => p.Action is ImportAction.Rejected or ImportAction.Skipped) ? 2 : 0;
    }
}
