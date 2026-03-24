using BellaBaxter.Client;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Ssh;

public class ConfigureSshSettings : CommandSettings
{
    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("-e|--environment <SLUG>")]
    public string? Environment { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class ConfigureSshCommand(BellaClientProvider provider, ContextService context, IOutputWriter output)
    : AsyncCommand<ConfigureSshSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext ctx, ConfigureSshSettings settings, CancellationToken ct)
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

            dynamic? result = null;
            await AnsiConsole.Status().StartAsync($"Configuring SSH CA for {envName}...", async _ =>
            {
                result = await client.Api.V1.Projects[projectSlug].Environments[envSlug].Ssh.Configure.PostAsync(cancellationToken: ct);
            });

            if (result is not null)
            {
                output.WriteSuccess(result.Message ?? "SSH CA configured.");
                if (!string.IsNullOrWhiteSpace(result.CaPublicKey as string))
                {
                    AnsiConsole.MarkupLine("\n[dim]CA Public Key:[/]");
                    AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(result.CaPublicKey as string ?? "")}[/]");
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
            output.WriteError($"Failed to configure SSH CA: {ex.Message}");
            return 1;
        }
    }
}
