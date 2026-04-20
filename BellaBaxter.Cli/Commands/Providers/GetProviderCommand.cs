using BellaBaxter.Client;
using BellaBaxter.Client.Models;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Providers;

public class GetProviderSettings : CommandSettings
{
    [CommandArgument(0, "<id-or-name>")]
    public string Identifier { get; init; } = "";

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class GetProviderCommand(BellaClientProvider provider, IOutputWriter output)
    : AsyncCommand<GetProviderSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, GetProviderSettings settings, CancellationToken ct)
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
            ProviderResponse? prov = null;
            await AnsiConsole.Status().StartAsync("Loading provider...", async _ =>
            {
                if (Guid.TryParse(settings.Identifier, out var guid))
                {
                    // Direct GUID lookup
                    prov = await client.Api.V1.Providers[guid].GetAsync(cancellationToken: ct);
                }
                else
                {
                    // Slug or name — resolve via list
                    var all = await client.Api.V1.Providers.GetAsync(cancellationToken: ct);
                    prov = all?.FirstOrDefault(p =>
                        string.Equals(p.Slug, settings.Identifier, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(p.Name, settings.Identifier, StringComparison.OrdinalIgnoreCase));
                    // Fetch full detail via GUID once resolved
                    if (prov?.Id is string idStr && Guid.TryParse(idStr, out var resolvedId))
                        prov = await client.Api.V1.Providers[resolvedId].GetAsync(cancellationToken: ct);
                }
            });

            if (prov is null)
            {
                output.WriteError($"Provider '{settings.Identifier}' not found.");
                return 1;
            }

            // Mask credential values in configuration
            var config = prov.Configuration?.AdditionalData
                .ToDictionary(kvp => kvp.Key, kvp =>
                {
                    var val = kvp.Value?.ToString() ?? "";
                    var lk = kvp.Key.ToLowerInvariant();
                    return lk.Contains("secret") || lk.Contains("key") || lk.Contains("password") ||
                           lk.Contains("token") || lk.Contains("credential") ? "***" : val;
                }) ?? new Dictionary<string, string>();

            output.WriteObject(new
            {
                id = prov.Id,
                name = prov.Name,
                slug = prov.Slug,
                type = prov.Type,
                description = prov.Description,
                status = prov.Status,
                source = prov.Source,
                createdBy = prov.CreatedBy,
                createdAt = prov.CreatedAt?.ToString(),
                updatedAt = prov.UpdatedAt?.ToString(),
                configuration = config
            });

            return 0;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to get provider: {ex.Message}");
            return 1;
        }
    }
}
