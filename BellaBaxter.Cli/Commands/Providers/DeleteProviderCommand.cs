using BellaBaxter.Client;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Providers;

public class DeleteProviderSettings : CommandSettings
{
    [CommandArgument(0, "<id>")]
    public string Id { get; init; } = "";

    [CommandOption("-f|--force")]
    public bool Force { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class DeleteProviderCommand(BellaClientProvider provider, IOutputWriter output)
    : AsyncCommand<DeleteProviderSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, DeleteProviderSettings settings, CancellationToken ct)
    {
        provider.ApplyOutputModeOverrides(settings.Json);

        BellaClient client;
        try { client = provider.CreateClient(); }
        catch (InvalidOperationException)
        {
            output.WriteError("Not logged in. Run 'bella login' first.");
            return 1;
        }

        if (!settings.Force)
        {
            if (Console.IsOutputRedirected || output is JsonOutputWriter)
            {
                output.WriteError("Use --force to delete without confirmation.");
                return 1;
            }
            var confirm = AnsiConsole.Confirm($"Delete provider [bold]{settings.Id}[/]?", defaultValue: false);
            if (!confirm)
            {
                output.WriteInfo("Cancelled.");
                return 0;
            }
        }

        try
        {
            await AnsiConsole.Status().StartAsync("Deleting provider...", async _ =>
            {
                await client.Api.V1.Providers[settings.Id].DeleteAsync(cancellationToken: ct);
            });

            output.WriteSuccess($"Provider '{settings.Id}' deleted.");
            return 0;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to delete provider: {ex.Message}");
            return 1;
        }
    }
}
