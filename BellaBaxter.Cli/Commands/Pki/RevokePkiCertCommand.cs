using BellaBaxter.Client;
using BellaBaxter.Client.Models;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Pki;

public class RevokePkiCertSettings : CommandSettings
{
    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("-e|--environment <SLUG>")]
    public string? Environment { get; init; }

    [CommandOption("-s|--serial <SERIAL>")]
    [System.ComponentModel.Description("Certificate serial number to revoke (e.g. 12:34:ab:cd:...)")]
    public string? SerialNumber { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class RevokePkiCertCommand(BellaClientProvider provider, ContextService context, IOutputWriter output)
    : AsyncCommand<RevokePkiCertSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext ctx, RevokePkiCertSettings settings, CancellationToken ct)
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

            var serial = settings.SerialNumber;
            if (string.IsNullOrWhiteSpace(serial))
            {
                if (Console.IsOutputRedirected || output is JsonOutputWriter)
                {
                    output.WriteError("--serial is required.");
                    return 1;
                }
                serial = AnsiConsole.Ask<string>("[bold]Certificate serial number:[/]");
            }

            if (!AnsiConsole.Confirm($"[red]Revoke certificate {serial}?[/] This cannot be undone."))
                return 0;

            PkiRevokeResponse? result = null;
            await AnsiConsole.Status().StartAsync("Revoking certificate...", async _ =>
            {
                result = await client.Api.V1.Projects[projectSlug].Environments[envSlug].Pki.Revoke.PostAsync(
                    new PkiRevokeRequest { SerialNumber = serial },
                    cancellationToken: ct);
            });

            if (result?.Success == true)
                output.WriteSuccess(result.Message ?? $"Certificate {serial} revoked.");
            else
                output.WriteError(result?.Message ?? "Revocation failed.");

            return result?.Success == true ? 0 : 1;
        }
        catch (InvalidOperationException ex)
        {
            output.WriteError(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to revoke certificate: {ex.Message}");
            return 1;
        }
    }
}
