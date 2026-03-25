using BellaBaxter.Client;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Totp;

public class DeleteTotpSettings : CommandSettings
{
    [CommandArgument(0, "<name>")]
    public string Name { get; init; } = "";

    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("-e|--environment <SLUG>")]
    public string? Environment { get; init; }

    [CommandOption("-f|--force")]
    public bool Force { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class DeleteTotpCommand(BellaClientProvider provider, ContextService context, IOutputWriter output)
    : AsyncCommand<DeleteTotpSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext ctx, DeleteTotpSettings settings, CancellationToken ct)
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

            if (!settings.Force && output is not JsonOutputWriter)
            {
                var confirm = AnsiConsole.Confirm($"Delete TOTP key [bold]{settings.Name}[/] from [bold]{envName}[/]?", defaultValue: false);
                if (!confirm)
                {
                    output.WriteInfo("Cancelled.");
                    return 0;
                }
            }

            await client.Api.V1.Projects[projectSlug].Environments[envSlug].Totp[settings.Name]
                .DeleteAsync(cancellationToken: ct);

            output.WriteSuccess($"TOTP key '[bold]{settings.Name}[/]' deleted from [bold]{envName}[/].");
            return 0;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to delete TOTP key: {ex.Message}");
            return 1;
        }
    }
}
