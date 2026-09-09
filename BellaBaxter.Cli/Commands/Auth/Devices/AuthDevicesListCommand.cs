using BellaBaxter.Client.Models;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Auth.Devices;

public class AuthDevicesListSettings : CommandSettings
{
    [System.ComponentModel.Description("Every device in the tenant, not just yours (Owner/Admin)")]
    [CommandOption("--all")]
    public bool All { get; set; }

    [System.ComponentModel.Description("Output format: table (default) or json")]
    [CommandOption("-o|--output <format>")]
    public string? Output { get; set; }
}

/// <summary>
/// bella auth devices list — the machines registered in this tenant. spec 037 US3.
/// </summary>
/// <remarks>
/// The question this answers is "which machines can read our secrets?", which before spec 037 had no
/// answer at all: nothing was registered, so nothing could be listed or withdrawn.
/// </remarks>
public class AuthDevicesListCommand(
    BellaClientProvider provider,
    IOutputWriter output
) : AsyncCommand<AuthDevicesListSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        AuthDevicesListSettings settings,
        CancellationToken ct
    )
    {
        List<ZkeDeviceResponse>? devices;
        try
        {
            var client = provider.CreateClient();
            devices = await client.Api.V1.Tenants.Me.Devices.GetAsync(
                r => r.QueryParameters.Scope = settings.All ? "tenant" : "mine",
                cancellationToken: ct);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 403)
        {
            output.WriteError("Listing every device in the tenant requires the Owner or Admin role.", "forbidden");
            return 1;
        }
        catch (Exception ex)
        {
            output.WriteError($"Could not list devices: {ex.Message}");
            return 1;
        }

        devices ??= [];

        if (string.Equals(settings.Output, "json", StringComparison.OrdinalIgnoreCase))
        {
            output.WriteObject(devices);
            return 0;
        }

        if (devices.Count == 0)
        {
            output.WriteInfo(
                settings.All
                    ? "No devices are registered in this tenant."
                    : "You have no registered devices here. Run 'bella auth setup' to register this machine.");
            return 0;
        }

        output.WriteTable(
            ["FINGERPRINT", "LABEL", "STATE", "REGISTERED", "LAST USED", "OWNER"],
            devices.Select(d => new[]
            {
                d.Fingerprint ?? "",
                d.Label ?? "",
                d.State ?? "",
                d.RegisteredAt?.ToString("yyyy-MM-dd") ?? "",
                // Derived from the audit trail, so "never" means genuinely never — not "we stopped counting".
                d.LastUsedAt?.ToString("yyyy-MM-dd HH:mm") ?? "never",
                d.RegisteredBy?.DisplayName ?? "",
            }));

        return 0;
    }
}
