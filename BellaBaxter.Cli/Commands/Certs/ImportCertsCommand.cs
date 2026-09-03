using System.Text.Json;
using System.Text.RegularExpressions;
using BellaBaxter.Client;
using BellaBaxter.Client.Models;
using BellaBaxter.Crypto.Certificates;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Microsoft.Kiota.Abstractions.Serialization;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Certs;

// spec 020 (US1) — `bella certs import`: ingests one issuer drop into managed certificate
// entries, in the exact value shape the rotation engine already reads.
//
// Sequence (contracts/cli-certs-import.md):
//   1. gate the certificate-source provider   — fails closed BEFORE reading any certificate
//   2. discover the drop                       — identity from the certificate, not file names
//   3. validate EVERYTHING                     — nothing is written until the whole drop is judged
//   4. read existing state once                — one call answers create/update/unchanged for all
//   5. write                                   — unless --dry-run; skip unchanged
//   6. report                                  — and set an exit code that means something
//
// CONFIDENTIALITY (FR-013): private keys and manifest passphrases never reach output, logs, or
// telemetry in any mode. The only place they go is the secret store.

public class ImportCertsSettings : CommandSettings
{
    [CommandOption("--dir <PATH>")]
    public string Directory { get; init; } = "";

    /// <summary>
    /// Slug of the <c>BellaBaxterSecretsSource</c> provider. Supplies the naming prefix and gates
    /// the run. NOT the write destination — that is the environment's secrets provider.
    /// </summary>
    [CommandOption("--provider <SLUG>")]
    public string Provider { get; init; } = "";

    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("-e|--env|--environment <SLUG>")]
    public string? Environment { get; init; }

    [CommandOption("--manifest <FILE>")]
    public string? Manifest { get; init; }

    [CommandOption("--dry-run")]
    public bool DryRun { get; init; }

    /// <summary>Write nothing at all if any certificate in the drop fails validation.</summary>
    [CommandOption("--strict")]
    public bool Strict { get; init; }

    /// <summary>Exclude a trailing self-signed root from the stored chain.</summary>
    [CommandOption("--strip-root")]
    public bool StripRoot { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }

    public override ValidationResult Validate()
    {
        if (string.IsNullOrWhiteSpace(Directory))
            return ValidationResult.Error("--dir is required.");
        if (!System.IO.Directory.Exists(Directory))
            return ValidationResult.Error($"Directory '{Directory}' not found.");
        if (string.IsNullOrWhiteSpace(Provider))
            return ValidationResult.Error("--provider is required (the certificate source's slug).");
        return ValidationResult.Success();
    }
}

public class ImportCertsCommand(
    BellaClientProvider provider,
    ContextService context,
    IOutputWriter output,
    SecretProviderResolver providerResolver
) : AsyncCommand<ImportCertsSettings>
{
    /// <summary>
    /// The declared secret type for an imported bundle (spec 020, US2). Lowercase wire spelling;
    /// the API parses it case-insensitively.
    /// </summary>
    private const string CertificateSecretType = "certificate";

    protected override async Task<int> ExecuteAsync(
        CommandContext ctx,
        ImportCertsSettings settings,
        CancellationToken ct
    )
    {
        provider.ApplyOutputModeOverrides(settings.Json);

        BellaClient client;
        try
        {
            client = provider.CreateClient();
        }
        catch (InvalidOperationException)
        {
            output.WriteError("Not logged in. Run 'bella login' first.");
            return 1;
        }

        try
        {
            var (projectSlug, _, _, envSlug, _, _) = await context.ResolveProjectEnvironmentAsync(
                settings.Project,
                settings.Environment,
                client,
                ct,
                strictJwtLocal: true,
                bootstrapBellaFromExplicit: true
            );

            // ── 1. Gate the certificate source ───────────────────────────────
            var gate = await ResolveCertificateSourceAsync(client, settings.Provider, ct);
            if (gate is null)
            {
                return 1;
            }

            var (sourceSlug, secretPrefix) = gate.Value;

            // ── 2. Discover the drop ─────────────────────────────────────────
            CertificateDrop drop;
            try
            {
                drop = CertificateDropReader.Read(settings.Directory, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                output.WriteError($"Could not read the drop: {ex.Message}");
                return 1;
            }

            if (drop.Entries.Count == 0)
            {
                output.WriteError(
                    $"'{settings.Directory}' has no subdirectories. A drop holds one folder per "
                        + "certificate — check whether the delivery is nested one level deeper."
                );
                return 1;
            }

            // ── Manifest (advisory only) ─────────────────────────────────────
            IReadOnlyList<ManifestRow> manifest = [];
            if (!string.IsNullOrWhiteSpace(settings.Manifest))
            {
                try
                {
                    manifest = ManifestReader.Read(settings.Manifest);
                }
                catch (ManifestFormatException ex)
                {
                    output.WriteError(ex.Message);
                    return 1;
                }
            }

            // ── 3. Validate everything before writing anything ───────────────
            // The whole drop is judged before a single write (FR-005). ImportPlanner holds the
            // rules; this method only moves bytes.
            var planned = ImportPlanner.Plan(
                drop,
                secretPrefix,
                manifest,
                settings.StripRoot,
                now: null,
                ct
            );

            var warnings = ImportPlanner.CrossCheckManifest(manifest, planned);
            var rejected = planned.Count(p => p.Action == ImportAction.Rejected);

            if (settings.Strict && rejected > 0)
            {
                Report(settings, sourceSlug, secretPrefix, projectSlug, envSlug, null, planned, warnings);
                output.WriteError(
                    $"--strict: {rejected} certificate(s) failed validation, so nothing was written."
                );
                return 2;
            }

            // ── 4. Read existing state once ──────────────────────────────────
            var existing = await ReadExistingSecretsAsync(client, projectSlug, envSlug, ct);
            if (existing is null)
            {
                return 1;
            }

            ImportPlanner.Refine(planned, existing);

            // ── 5. Write ─────────────────────────────────────────────────────
            string? destinationSlug = null;
            var writeFailure = false;

            if (!settings.DryRun && planned.Any(p => p.Action is ImportAction.Created or ImportAction.Updated))
            {
                destinationSlug = await providerResolver.ResolveAsync(
                    client,
                    projectSlug,
                    envSlug,
                    explicitSlug: null,
                    ct
                );
                if (destinationSlug is null)
                {
                    return 1;
                }

                writeFailure = !await WriteAsync(
                    client,
                    projectSlug,
                    envSlug,
                    destinationSlug,
                    planned,
                    ct
                );
            }

            // ── 6. Report ────────────────────────────────────────────────────
            Report(
                settings,
                sourceSlug,
                secretPrefix,
                projectSlug,
                envSlug,
                destinationSlug,
                planned,
                warnings
            );

            return ImportPlanner.ExitCode(planned, writeFailure);
        }
        catch (OperationCanceledException)
        {
            output.WriteError("Cancelled. Already-stored certificates are intact; re-running is safe.");
            return 3;
        }
        catch (InvalidOperationException ex)
        {
            output.WriteError(ex.Message);
            return 1;
        }
    }

    // ── Provider gate (T025) ─────────────────────────────────────────────────

    /// <summary>
    /// Resolves the certificate-source provider and its prefix. Refuses any other provider type
    /// BEFORE a single certificate is read, so an import cannot be aimed at the appliance, DNS,
    /// or deploy-scripts provider by typo (FR-008).
    /// </summary>
    private async Task<(string Slug, string Prefix)?> ResolveCertificateSourceAsync(
        BellaClient client,
        string slugOrId,
        CancellationToken ct
    )
    {
        ProviderResponse? found = null;
        try
        {
            if (Guid.TryParse(slugOrId, out var id))
            {
                found = await client.Api.V1.Providers[id].GetAsync(cancellationToken: ct);
            }
            else
            {
                var all = await client.Api.V1.Providers.GetAsync(cancellationToken: ct);
                var match = all?.FirstOrDefault(p =>
                    string.Equals(p.Slug, slugOrId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(p.Name, slugOrId, StringComparison.OrdinalIgnoreCase)
                );
                if (match?.Id is string idText && Guid.TryParse(idText, out var resolvedId))
                {
                    found = await client.Api.V1.Providers[resolvedId].GetAsync(cancellationToken: ct);
                }
            }
        }
        catch (Exception ex)
        {
            output.WriteError($"Could not read provider '{slugOrId}': {ex.Message}");
            return null;
        }

        if (found is null)
        {
            output.WriteError($"Provider '{slugOrId}' was not found.");
            return null;
        }

        var prefix = ReadConfigValue(found.Configuration?.AdditionalData, "secret_prefix");
        var refusal = ImportPlanner.RefuseSource(found.Slug ?? slugOrId, found.Type, prefix);
        if (refusal is not null)
        {
            output.WriteError(refusal);
            return null;
        }

        return (found.Slug ?? slugOrId, prefix!);
    }

    /// <summary>Reads one configuration value, unwrapping Kiota's untyped representation.</summary>
    private static string? ReadConfigValue(IDictionary<string, object>? config, string key)
    {
        if (config is null)
        {
            return null;
        }

        var entry = config.FirstOrDefault(kvp =>
            string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase)
        );

        return entry.Value switch
        {
            null => null,
            string text => text,
            UntypedString untyped => untyped.GetValue(),
            var other => other.ToString(),
        };
    }

    // ── Existing state (T027) ────────────────────────────────────────────────

    private async Task<Dictionary<string, string>?> ReadExistingSecretsAsync(
        BellaClient client,
        string projectSlug,
        string envSlug,
        CancellationToken ct
    )
    {
        try
        {
            var response = await client
                .Api.V1.Projects[projectSlug]
                .Environments[envSlug]
                .Secrets.GetAsync(cancellationToken: ct);

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var entries =
                response?.Secrets?.AdditionalData
                ?? (IDictionary<string, object>)new Dictionary<string, object>();
            foreach (var kvp in entries)
            {
                result[kvp.Key] = kvp.Value switch
                {
                    string text => text,
                    UntypedString untyped => untyped.GetValue() ?? string.Empty,
                    var other => other?.ToString() ?? string.Empty,
                };
            }

            return result;
        }
        catch (Exception ex)
        {
            output.WriteError($"Could not read the environment's current secrets: {ex.Message}");
            return null;
        }
    }

    // ── Writing (T030, T031) ─────────────────────────────────────────────────

    /// <summary>Writes every planned create/update. Returns false when a write failed.</summary>
    private async Task<bool> WriteAsync(
        BellaClient client,
        string projectSlug,
        string envSlug,
        string destinationSlug,
        List<PlannedCertificate> planned,
        CancellationToken ct
    )
    {
        var secrets = client
            .Api.V1.Projects[projectSlug]
            .Environments[envSlug]
            .Providers[destinationSlug]
            .Secrets;

        for (var i = 0; i < planned.Count; i++)
        {
            var item = planned[i];
            if (item.Action is not (ImportAction.Created or ImportAction.Updated))
            {
                continue;
            }

            ct.ThrowIfCancellationRequested();

            try
            {
                if (item.Action == ImportAction.Created)
                {
                    await secrets.PostAsync(
                        new CreateSecretRequest
                        {
                            Key = item.SecretKey,
                            Value = item.Value,
                            Description = $"TLS certificate for {item.CommonName}",
                            // spec 020 (US2): declaring the type is what makes the platform parse
                            // the bundle, record its facts, and set its expiry — so imported
                            // certificates join the expiry inventory automatically.
                            Type = CertificateSecretType,
                        },
                        cancellationToken: ct
                    );
                }
                else
                {
                    await secrets[item.SecretKey!]
                        .PutAsync(
                            new UpdateSecretRequest
                            {
                                Value = item.Value,
                                Description = $"TLS certificate for {item.CommonName}",
                                Type = CertificateSecretType,
                            },
                            cancellationToken: ct
                        );
                }
            }
            catch (Exception ex)
            {
                // Stop cleanly: say which certificate we stopped at, leave what is already
                // stored intact, and stay re-runnable (FR-018/FR-019).
                planned[i] = item with { Action = ImportAction.Rejected, Reason = ex.Message };
                output.WriteError(
                    $"Writing '{item.SecretKey}' failed: {ex.Message}. Certificates already stored "
                        + "are intact — fix the cause and re-run; the import is repeatable."
                );
                return false;
            }
        }

        return true;
    }

    // ── Reporting (T032, T033) ───────────────────────────────────────────────

    private void Report(
        ImportCertsSettings settings,
        string sourceSlug,
        string secretPrefix,
        string projectSlug,
        string envSlug,
        string? destinationSlug,
        List<PlannedCertificate> planned,
        List<string> warnings
    )
    {
        var now = DateTimeOffset.UtcNow;

        if (output is JsonOutputWriter || settings.Json)
        {
            output.WriteObject(
                new
                {
                    provider = new { slug = sourceSlug, secretPrefix },
                    target = new
                    {
                        project = projectSlug,
                        environment = envSlug,
                        secretProvider = destinationSlug,
                    },
                    dryRun = settings.DryRun,
                    summary = new
                    {
                        created = planned.Count(p => p.Action == ImportAction.Created),
                        updated = planned.Count(p => p.Action == ImportAction.Updated),
                        unchanged = planned.Count(p => p.Action == ImportAction.Unchanged),
                        rejected = planned.Count(p => p.Action == ImportAction.Rejected),
                        skipped = planned.Count(p => p.Action == ImportAction.Skipped),
                    },
                    certificates = planned
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
                        .ToList(),
                    warnings = warnings.Select(w => new { kind = "manifest", note = w }).ToList(),
                    rejections = planned
                        .Where(p => p.Action is ImportAction.Rejected or ImportAction.Skipped)
                        .Select(p => new
                        {
                            sourceDirectory = p.SourceDirectory,
                            commonName = p.CommonName,
                            reason = p.Reason,
                        })
                        .ToList(),
                }
            );
            return;
        }

        var rows = planned
            .Where(p => p.Facts is not null)
            .OrderBy(p => p.Facts!.NotAfter)
            .Select(p => new[]
            {
                p.CommonName ?? "",
                p.Facts!.NotAfter.ToString("yyyy-MM-dd"),
                p.Facts.DaysRemaining(now).ToString(),
                ShortIssuer(p.Facts.Issuer),
                p.Action.ToString().ToLowerInvariant(),
            })
            .ToList();

        if (rows.Count > 0)
        {
            output.WriteTable(
                ["Common Name", "Expires", "Days", "Issuer", "Action"],
                rows
            );
        }

        foreach (var warning in warnings)
        {
            output.WriteWarning(warning);
        }

        foreach (var item in planned.Where(p => p.Action is ImportAction.Rejected or ImportAction.Skipped))
        {
            var label = item.CommonName ?? item.SourceDirectory;
            output.WriteError($"{label}: {item.Reason}");
        }

        var created = planned.Count(p => p.Action == ImportAction.Created);
        var updated = planned.Count(p => p.Action == ImportAction.Updated);
        var unchanged = planned.Count(p => p.Action == ImportAction.Unchanged);
        var verb = settings.DryRun ? "would be" : "were";

        output.WriteInfo(
            $"{created} created, {updated} updated, {unchanged} unchanged ({verb} written to "
                + $"{destinationSlug ?? "the environment's secrets provider"}); prefix '{secretPrefix}'."
        );

        if (settings.DryRun)
        {
            output.WriteInfo("Dry run — nothing was written.");
        }
    }

    /// <summary>The issuer's common name alone; a full distinguished name overwhelms the table.</summary>
    private static string ShortIssuer(string issuer)
    {
        foreach (var part in issuer.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[3..];
            }
        }

        return issuer;
    }
}
