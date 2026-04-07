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

    [CommandOption("-e|--environment <SLUG>")]
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
    ZkeService zke
) : AsyncCommand<ListSecretsSettings>
{
    public override async Task<int> ExecuteAsync(
        CommandContext ctx,
        ListSecretsSettings settings,
        CancellationToken ct
    )
    {
        provider.ApplyOutputModeOverrides(settings.Json);

        // ZKE: upgrade to a ZkeDekHandler client when a device key or --private-key is available.
        ECDiffieHellman? zkeEcdh = null;
        if (!string.IsNullOrEmpty(settings.PrivateKey))
        {
            var pkcs8b64 = ZkeService.ResolvePrivateKeyFromUrl(settings.PrivateKey);
            if (pkcs8b64 is not null)
            {
                zkeEcdh = ECDiffieHellman.Create();
                zkeEcdh.ImportPkcs8PrivateKey(Convert.FromBase64String(pkcs8b64), out _);
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]⚠ Could not resolve --private-key; ZKE disabled.[/]");
            }
        }
        else
        {
            zkeEcdh = zke.LoadEcdhKey();
        }

        BellaClient client;
        try
        {
            if (zkeEcdh is not null)
            {
                var zkeHandler = new ZkeDekHandler(zkeEcdh, onWrappedDekReceived: null);
                client = provider.CreateClientWithZke(zkeHandler);
                AnsiConsole.MarkupLine("[dim]🔐 ZKE enabled — secrets will be decrypted locally.[/]");
            }
            else
            {
                client = provider.CreateClient();
            }
        }
        catch (InvalidOperationException)
        {
            output.WriteError("Not logged in. Run 'bella login' first.");
            return 1;
        }

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

            await AnsiConsole
                .Status()
                .StartAsync(
                    "Loading secrets...",
                    async _ =>
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
}
