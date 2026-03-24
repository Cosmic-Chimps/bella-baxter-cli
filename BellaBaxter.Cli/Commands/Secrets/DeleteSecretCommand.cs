using BellaBaxter.Client;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Secrets;

public class DeleteSecretSettings : CommandSettings
{
    [CommandArgument(0, "<key>")]
    public string Key { get; init; } = "";

    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("-e|--environment <SLUG>")]
    public string? Environment { get; init; }

    [CommandOption("-f|--force")]
    public bool Force { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class DeleteSecretCommand(BellaClientProvider provider, ContextService context, IOutputWriter output)
    : AsyncCommand<DeleteSecretSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext ctx, DeleteSecretSettings settings, CancellationToken ct)
    {
        provider.ApplyOutputModeOverrides(settings.Json);

        BellaClient client;
        try { client = provider.CreateClient(); }
        catch (InvalidOperationException)
        {
            output.WriteError("Not logged in. Run 'bella login' first.");
            return 1;
        }

        try
        {
            var (projectSlug, _, _) = await context.ResolveProjectAsync(settings.Project, client, ct);
            var (envSlug, _, _) = await context.ResolveEnvironmentAsync(settings.Environment, projectSlug, client, ct);

            if (!settings.Force)
            {
                if (Console.IsOutputRedirected || output is JsonOutputWriter)
                {
                    output.WriteError("Use --force to delete without confirmation.");
                    return 1;
                }
                var confirm = AnsiConsole.Confirm($"Delete secret [bold]{settings.Key}[/]?", defaultValue: false);
                if (!confirm)
                {
                    output.WriteInfo("Cancelled.");
                    return 0;
                }
            }

            var providers = await client.Api.V1.Projects[projectSlug].Environments[envSlug].Providers.GetAsync(cancellationToken: ct);
            var providerList = providers ?? [];
            if (providerList.Count == 0)
            {
                output.WriteError("No providers assigned to this environment.");
                return 1;
            }

            var providerSlug = providerList[0].ProviderSlug ?? providerList[0].ProviderId ?? "";

            await AnsiConsole.Status().StartAsync($"Deleting secret {settings.Key}...", async _ =>
            {
                await client.Api.V1.Projects[projectSlug].Environments[envSlug]
                    .Providers[providerSlug].Secrets[settings.Key].DeleteAsync(cancellationToken: ct);
            });

            output.WriteSuccess($"Secret '{settings.Key}' deleted.");
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            output.WriteError(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to delete secret: {ex.Message}");
            return 1;
        }
    }
}
