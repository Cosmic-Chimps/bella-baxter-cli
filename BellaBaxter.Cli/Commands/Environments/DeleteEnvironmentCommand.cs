using BellaBaxter.Client;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Environments;

public class DeleteEnvironmentSettings : CommandSettings
{
    [CommandArgument(0, "[slug]")]
    public string? Slug { get; init; }

    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("-f|--force")]
    public bool Force { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class DeleteEnvironmentCommand(BellaClientProvider provider, ContextService context, IOutputWriter output)
    : AsyncCommand<DeleteEnvironmentSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext ctx, DeleteEnvironmentSettings settings, CancellationToken ct)
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
            var (envSlug, _, _) = await context.ResolveEnvironmentAsync(settings.Slug, projectSlug, client, ct);

            if (!settings.Force)
            {
                if (Console.IsOutputRedirected || output is JsonOutputWriter)
                {
                    output.WriteError("Use --force to delete without confirmation.");
                    return 1;
                }
                var confirm = AnsiConsole.Confirm($"Delete environment [bold]{envSlug}[/] in project [bold]{projectSlug}[/]?", defaultValue: false);
                if (!confirm)
                {
                    output.WriteInfo("Cancelled.");
                    return 0;
                }
            }

            await AnsiConsole.Status().StartAsync("Deleting environment...", async _ =>
            {
                await client.Api.V1.Projects[projectSlug].Environments[envSlug].DeleteAsync(cancellationToken: ct);
            });

            output.WriteSuccess($"Environment '{envSlug}' deleted.");
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            output.WriteError(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to delete environment: {ex.Message}");
            return 1;
        }
    }
}
