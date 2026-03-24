using BellaBaxter.Client;
using BellaBaxter.Client.Models;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Providers;

public class ListProvidersSettings : CommandSettings
{
    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class ListProvidersCommand(BellaClientProvider provider, IOutputWriter output)
    : AsyncCommand<ListProvidersSettings>
{
    private static string ProviderIcon(string? type) => type?.ToLowerInvariant() switch
    {
        var t when t?.StartsWith("aws") == true => "🟠",
        var t when t?.StartsWith("azure") == true => "🔵",
        "vault" => "🔐",
        var t when t?.StartsWith("google") == true => "☁️",
        var t when t?.StartsWith("http") == true => "🔌",
        _ => "📦"
    };

    public override async Task<int> ExecuteAsync(CommandContext context, ListProvidersSettings settings, CancellationToken ct)
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
            List<ProviderResponse>? providers = null;
            await AnsiConsole.Status().StartAsync("Loading providers...", async _ =>
            {
                providers = await client.Api.V1.Providers.GetAsync(cancellationToken: ct);
            });

            var list = providers ?? [];
            if (list.Count == 0)
            {
                output.WriteInfo("No providers found.");
                return 0;
            }

            output.WriteTable(
                ["", "Name", "Slug", "Type", "Description", "Created"],
                list.Select(p => new[]
                {
                    ProviderIcon(p.Type),
                    p.Name ?? "",
                    p.Slug ?? "",
                    p.Type ?? "",
                    p.Description ?? "",
                    p.CreatedAt?.ToString() ?? ""
                }));

            return 0;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to list providers: {ex.Message}");
            return 1;
        }
    }
}
