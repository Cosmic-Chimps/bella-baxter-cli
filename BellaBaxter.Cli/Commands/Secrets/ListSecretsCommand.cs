using System.Security.Cryptography;
using BellaBaxter.Client;
using BellaBaxter.Client.Models;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Secrets;

public class ListSecretsSettings : CommandSettings
{
    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("-e|--env|--environment <SLUG>")]
    public string? Environment { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }

    /// <summary>
    /// Path or URL to the PKCS#8 P-256 private key for ZKE (Zero-Knowledge Encryption).
    /// Supports: file:///path/key.pem, env://VAR_NAME, or a bare file path.
    /// When omitted, the device key from <c>bella auth setup</c> is used if available.
    /// </summary>
    [CommandOption("--private-key <URL>")]
    public string? PrivateKey { get; init; }
}

public class ListSecretsCommand(
    BellaClientProvider provider,
    ContextService context,
    IOutputWriter output,
    ZkeClientSelection zkeSelection
) : AsyncCommand<ListSecretsSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext ctx,
        ListSecretsSettings settings,
        CancellationToken ct
    )
    {
        provider.ApplyOutputModeOverrides(settings.Json);

        // spec 037 — one place decides the client and whether this read may proceed at all.
        var selection = await zkeSelection.SelectAsync(settings.PrivateKey, appClientOverride: null, announce: true, ct);
        if (selection.Stopped)
            return selection.ExitCode!.Value;

        var client = selection.Client!;

        try
        {
            var (projectSlug, projectName, _, envSlug, envName, _) =
                await context.ResolveProjectEnvironmentAsync(
                    settings.Project,
                    settings.Environment,
                    client,
                    ct,
                    strictJwtLocal: true,
                    bootstrapBellaFromExplicit: true
                );

            List<EnvironmentProviderResponse>? providers = null;
            ListGlobalSecretsResponse? globalResp = null;

            await output.StatusAsync("Loading secrets...", async () =>
                    {
                        providers = await client
                            .Api.V1.Projects[projectSlug]
                            .Environments[envSlug]
                            .Providers.GetAsync(cancellationToken: ct);

                        try
                        {
                            globalResp = await client
                                .Api.V1.Projects[projectSlug]
                                .Secrets.GetAsync(cancellationToken: ct);
                        }
                        catch
                        { /* global secrets may not be configured for this project */
                        }
                    }
                );

            var providerList = providers ?? [];
            var globalSecrets = globalResp?.Secrets ?? [];

            if (providerList.Count == 0 && globalSecrets.Count == 0)
            {
                output.WriteInfo(
                    $"No providers or global secrets found for environment '{envName}'."
                );
                return 0;
            }

            output.WriteInfo($"Secrets in {projectName}/{envName}:");

            // ── Global (project-level) secrets ────────────────────────────────
            if (globalSecrets.Count > 0)
            {
                output.WriteInfo("\n  Global secrets (project-level):");
                output.WriteTable(
                    ["Key", "Type", "Value"],
                    globalSecrets.Select(s => new[] { s.Key ?? "", s.Type ?? "string", "***" })
                );
            }

            // ── Per-provider secrets ──────────────────────────────────────────
            foreach (var prov in providerList)
            {
                if (!IsSecretsProvider(prov))
                {
                    AnsiConsole.MarkupLine(
                        $"[dim]  Skipping {prov.ProviderName} ({prov.ProviderType}) — not a secrets storage provider.[/]"
                    );
                    continue;
                }

                output.WriteInfo($"\n  Provider: {prov.ProviderName} ({prov.ProviderType})");
                try
                {
                    var payload = await client
                        .Api.V1.Projects[projectSlug]
                        .Environments[envSlug]
                        .Providers[prov.ProviderSlug ?? prov.ProviderId ?? ""]
                        .Secrets.GetAsync(cancellationToken: ct);

                    if (payload?.AdditionalData.TryGetValue("secrets", out var rawSecrets) == true)
                    {
                        var secretsDict = rawSecrets.ToStringDict();
                        if (secretsDict.Count == 0)
                        {
                            output.WriteInfo("  (no secrets)");
                        }
                        else
                        {
                            output.WriteTable(
                                ["Key", "Value"],
                                secretsDict.Keys.Select(k => new[] { k, "***" })
                            );
                        }
                    }
                    else
                    {
                        output.WriteInfo("  (could not read secrets)");
                    }
                }
                catch (Exception ex)
                {
                    output.WriteWarning(
                        $"  Could not read secrets from {prov.ProviderName}: {ex.Message}"
                    );
                }
            }

            return 0;
        }
        catch (InvalidOperationException ex)
        {
            output.WriteError(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to list secrets: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Returns true for providers whose MetaType is Secrets — i.e. they support secrets CRUD
    /// via the secrets API. Falls back to ProviderType check for older API versions that do not
    /// yet return ProviderMetaType.
    /// </summary>
    private static bool IsSecretsProvider(EnvironmentProviderResponse prov) =>
        prov.ProviderMetaType is not null
            ? prov.ProviderMetaType == "Secrets"
            : prov.ProviderType switch
            {
                "Vault" => true,
                "AwsSecretsManager" => true,
                "AwsParameterStore" => true,
                "AzureKeyVault" => true,
                "GoogleSecretManager" => true,
                _ => false,
            };
}
