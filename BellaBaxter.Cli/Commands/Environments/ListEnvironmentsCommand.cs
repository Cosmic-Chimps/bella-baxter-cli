using BellaBaxter.Client;
using BellaBaxter.Client.Models;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Environments;

public class ListEnvironmentsSettings : CommandSettings
{
    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class ListEnvironmentsCommand(BellaClientProvider provider, ContextService context, IOutputWriter output)
    : AsyncCommand<ListEnvironmentsSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext ctx, ListEnvironmentsSettings settings, CancellationToken ct)
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
            var (projectSlug, projectName, _) = await context.ResolveProjectAsync(settings.Project, client, ct);

            List<EnvironmentResponse>? envs = null;
            await AnsiConsole.Status().StartAsync($"Loading environments for {projectName}...", async _ =>
            {
                envs = await client.Api.V1.Projects[projectSlug].Environments.GetAsync(cancellationToken: ct);
            });

            var list = envs ?? [];
            if (list.Count == 0)
            {
                output.WriteInfo($"No environments found in project '{projectName}'.");
                return 0;
            }

            output.WriteTable(
                ["Name", "ID", "Slug", "Description", "Providers", "Secrets", "Updated"],
                list.Select(e => new[]
                {
                    e.Name ?? "",
                    e.Id ?? "",
                    e.Slug ?? "",
                    e.Description ?? "",
                    e.ProviderCount?.ToString() ?? "0",
                    e.SecretCount?.ToString() ?? "0",
                    e.UpdatedAt?.ToString() ?? ""
                }));

            return 0;
        }
        catch (InvalidOperationException ex)
        {
            output.WriteError(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to list environments: {ex.Message}");
            return 1;
        }
    }
}
