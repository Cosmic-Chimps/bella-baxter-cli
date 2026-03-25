using BellaBaxter.Client;
using BellaBaxter.Client.Models;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Totp;

public class GenerateTotpSettings : CommandSettings
{
    [CommandArgument(0, "<name>")]
    public string Name { get; init; } = "";

    [CommandOption("-i|--issuer <ISSUER>")]
    public string? Issuer { get; init; }

    [CommandOption("-a|--account <ACCOUNT>")]
    public string? AccountName { get; init; }

    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("-e|--environment <SLUG>")]
    public string? Environment { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class GenerateTotpCommand(BellaClientProvider provider, ContextService context, IOutputWriter output)
    : AsyncCommand<GenerateTotpSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext ctx, GenerateTotpSettings settings, CancellationToken ct)
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

            var issuer = settings.Issuer;
            var account = settings.AccountName;

            if (output is not JsonOutputWriter)
            {
                issuer ??= AnsiConsole.Ask<string>("Issuer (e.g. MyApp):");
                account ??= AnsiConsole.Ask<string>("Account name (e.g. user@example.com):");
            }

            if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(account))
            {
                output.WriteError("--issuer and --account are required in non-interactive mode.");
                return 1;
            }

            TotpKeyImportResponse? result = null;
            await AnsiConsole.Status().StartAsync($"Generating TOTP key '{settings.Name}'...", async _ =>
            {
                result = await client.Api.V1.Projects[projectSlug].Environments[envSlug].Totp.Generate
                    .PostAsync(new GenerateTotpKeyRequest
                    {
                        Name = settings.Name,
                        Issuer = issuer,
                        AccountName = account
                    }, cancellationToken: ct);
            });

            if (output is JsonOutputWriter)
            {
                output.WriteObject(new { name = result?.Name, otpauthUrl = result?.OtpauthUrl, environment = envName });
                return 0;
            }

            output.WriteSuccess($"TOTP key '{result?.Name ?? settings.Name}' generated in {envName}.");
            if (!string.IsNullOrEmpty(result?.OtpauthUrl))
                AnsiConsole.MarkupLine($"[dim]otpauth URL: {result.OtpauthUrl}[/]");
            if (!string.IsNullOrEmpty(result?.QrCodeBase64))
                AnsiConsole.MarkupLine("[dim]QR code available in the web console.[/]");
            AnsiConsole.MarkupLine($"[dim]Run 'bella totp code {settings.Name}' to get the current code.[/]");
            return 0;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to generate TOTP key: {ex.Message}");
            return 1;
        }
    }
}
