using BellaBaxter.Client;
using BellaBaxter.Client.Models;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Totp;

public class ImportTotpSettings : CommandSettings
{
    [CommandArgument(0, "<name>")]
    public string Name { get; init; } = "";

    [CommandArgument(1, "<otpauth-url>")]
    public string OtpauthUrl { get; init; } = "";

    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("-e|--environment <SLUG>")]
    public string? Environment { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class ImportTotpCommand(BellaClientProvider provider, ContextService context, IOutputWriter output)
    : AsyncCommand<ImportTotpSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext ctx, ImportTotpSettings settings, CancellationToken ct)
    {
        provider.ApplyOutputModeOverrides(settings.Json);

        if (!settings.OtpauthUrl.StartsWith("otpauth://"))
        {
            output.WriteError("Invalid OTP auth URL. Must start with 'otpauth://'.");
            return 1;
        }

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

            TotpKeyImportResponse? result = null;
            await AnsiConsole.Status().StartAsync($"Importing TOTP key '{settings.Name}'...", async _ =>
            {
                result = await client.Api.V1.Projects[projectSlug].Environments[envSlug].Totp.Import
                    .PostAsync(new ImportTotpKeyRequest { Name = settings.Name, OtpauthUrl = settings.OtpauthUrl },
                        cancellationToken: ct);
            });

            if (output is JsonOutputWriter)
            {
                output.WriteObject(new { name = result?.Name, otpauthUrl = result?.OtpauthUrl, environment = envName });
                return 0;
            }

            output.WriteSuccess($"TOTP key '{result?.Name ?? settings.Name}' imported into {envName}.");
            if (!string.IsNullOrEmpty(result?.QrCodeBase64))
                AnsiConsole.MarkupLine("[dim]QR code available in the web console.[/]");
            return 0;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to import TOTP key: {ex.Message}");
            return 1;
        }
    }
}
