using BellaBaxter.Client;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Ssh;

public class DeleteSshRoleSettings : CommandSettings
{
    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("-e|--environment <SLUG>")]
    public string? Environment { get; init; }

    [CommandOption("-n|--name <ROLE>")]
    public string? Name { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class DeleteSshRoleCommand(BellaClientProvider provider, ContextService context, IOutputWriter output)
    : AsyncCommand<DeleteSshRoleSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext ctx, DeleteSshRoleSettings settings, CancellationToken ct)
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

            var roleName = settings.Name;
            if (string.IsNullOrWhiteSpace(roleName))
            {
                if (Console.IsOutputRedirected || output is JsonOutputWriter)
                {
                    output.WriteError("--name is required in non-interactive mode.");
                    return 1;
                }
                roleName = AnsiConsole.Prompt(new TextPrompt<string>("Role [bold]name[/] to delete:").PromptStyle("red"));
            }

            if (!Console.IsOutputRedirected && output is not JsonOutputWriter)
            {
                var confirmed = AnsiConsole.Prompt(
                    new ConfirmationPrompt($"Delete SSH role [bold red]{roleName}[/]? This cannot be undone.") { DefaultValue = false });
                if (!confirmed)
                {
                    output.WriteInfo("Cancelled.");
                    return 0;
                }
            }

            await AnsiConsole.Status().StartAsync($"Deleting SSH role '{roleName}' from {envName}...", async _ =>
            {
                await client.Api.V1.Projects[projectSlug].Environments[envSlug].Ssh.Roles[roleName].DeleteAsync(cancellationToken: ct);
            });

            output.WriteSuccess($"SSH role '{roleName}' deleted.");
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            output.WriteError(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to delete SSH role: {ex.Message}");
            return 1;
        }
    }
}
