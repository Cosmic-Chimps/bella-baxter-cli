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
}

public class ListSecretsCommand(
    BellaClientProvider provider,
    ContextService context,
    IOutputWriter output
) : AsyncCommand<ListSecretsSettings>
{
    public override async Task<int> ExecuteAsync(
        CommandContext ctx,
        ListSecretsSettings settings,
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
            var (projectSlug, projectName, _) = await context.ResolveProjectAsync(
                settings.Project,
                client,
                ct
            );
            var (envSlug, envName, _) = await context.ResolveEnvironmentAsync(
                settings.Environment,
                projectSlug,
                client,
                ct
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
                        catch { /* global secrets may not be configured for this project */ }
                    }
                );

            var providerList = providers ?? [];
            var globalSecrets = globalResp?.Secrets ?? [];

            if (providerList.Count == 0 && globalSecrets.Count == 0)
            {
                output.WriteInfo($"No providers or global secrets found for environment '{envName}'.");
                return 0;
            }

            output.WriteInfo($"Secrets in {projectName}/{envName}:");

            // ── Global (project-level) secrets ────────────────────────────────
            if (globalSecrets.Count > 0)
            {
                output.WriteInfo("\n  Global secrets (project-level):");
                output.WriteTable(
                    ["Key", "Type", "Value"],
                    globalSecrets.Select(s => new[]
                    {
                        s.Key ?? "",
                        s.Type ?? "string",
                        "***"
                    })
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
