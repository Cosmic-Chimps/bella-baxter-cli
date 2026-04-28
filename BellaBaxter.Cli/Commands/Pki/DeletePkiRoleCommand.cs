using BellaBaxter.Client;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Pki;

public class DeletePkiRoleSettings : CommandSettings
{
    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("-e|--env|--environment <SLUG>")]
    public string? Environment { get; init; }

    [CommandOption("--name <NAME>")]
    [System.ComponentModel.Description("Role name to delete")]
    public string? Name { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class DeletePkiRoleCommand(BellaClientProvider provider, ContextService context, IOutputWriter output)
    : AsyncCommand<DeletePkiRoleSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext ctx, DeletePkiRoleSettings settings, CancellationToken ct)
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
            var (envSlug, envName, _) = await context.ResolveEnvironmentAsync(settings.Environment, projectSlug, client, ct);

            var name = settings.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                if (Console.IsOutputRedirected || output is JsonOutputWriter)
                {
                    output.WriteError("--name is required.");
                    return 1;
                }
                name = AnsiConsole.Ask<string>("[bold]Role name to delete:[/]");
            }

            if (!AnsiConsole.Confirm($"[red]Delete PKI role '{name}'?[/]"))
                return 0;

            await AnsiConsole.Status().StartAsync($"Deleting PKI role '{name}'...", async _ =>
            {
                await client.Api.V1.Projects[projectSlug].Environments[envSlug].Pki.Roles[name].DeleteAsync(cancellationToken: ct);
            });

            output.WriteSuccess($"PKI role '{name}' deleted.");
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            output.WriteError(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to delete PKI role: {ex.Message}");
            return 1;
        }
    }
}
