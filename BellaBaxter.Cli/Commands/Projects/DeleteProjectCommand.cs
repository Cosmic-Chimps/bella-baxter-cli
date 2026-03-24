using BellaBaxter.Client;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Projects;

public class DeleteProjectSettings : CommandSettings
{
    [CommandArgument(0, "<identifier>")]
    public string Identifier { get; init; } = "";

    [CommandOption("-f|--force")]
    public bool Force { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class DeleteProjectCommand(BellaClientProvider provider, IOutputWriter output)
    : AsyncCommand<DeleteProjectSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, DeleteProjectSettings settings, CancellationToken ct)
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
            if (!settings.Force)
            {
                if (Console.IsOutputRedirected || output is JsonOutputWriter)
                {
                    output.WriteError("Use --force to delete without confirmation in non-interactive mode.");
                    return 1;
                }
                var confirm = AnsiConsole.Confirm($"Delete project [bold]{settings.Identifier}[/]?", defaultValue: false);
                if (!confirm)
                {
                    output.WriteInfo("Cancelled.");
                    return 0;
                }
            }

            await AnsiConsole.Status().StartAsync("Deleting project...", async _ =>
            {
                await client.Api.V1.Projects[settings.Identifier].DeleteAsync(cancellationToken: ct);
            });

            output.WriteSuccess($"Project '{settings.Identifier}' deleted.");
            return 0;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to delete project: {ex.Message}");
            return 1;
        }
    }
}
