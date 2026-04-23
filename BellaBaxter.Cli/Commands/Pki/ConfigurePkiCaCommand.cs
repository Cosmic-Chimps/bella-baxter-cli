using BellaBaxter.Client;
using BellaBaxter.Client.Models;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Pki;

public class ConfigurePkiCaSettings : CommandSettings
{
    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("-e|--environment <SLUG>")]
    public string? Environment { get; init; }

    [CommandOption("--common-name <CN>")]
    [System.ComponentModel.Description("CA common name (e.g. 'My Org Root CA')")]
    public string? CommonName { get; init; }

    [CommandOption("--organization <ORG>")]
    public string? Organization { get; init; }

    [CommandOption("--country <CC>")]
    [System.ComponentModel.Description("Two-letter country code (e.g. US)")]
    public string? Country { get; init; }

    [CommandOption("--key-type <TYPE>")]
    [System.ComponentModel.Description("Key type: rsa or ec (default: rsa)")]
    public string? KeyType { get; init; }

    [CommandOption("--key-bits <BITS>")]
    [System.ComponentModel.Description("Key size in bits (default: 2048 for RSA, 256 for EC)")]
    public int? KeyBits { get; init; }

    [CommandOption("--ttl <TTL>")]
    [System.ComponentModel.Description("CA certificate lifetime (default: 87600h / 10 years)")]
    public string? Ttl { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class ConfigurePkiCaCommand(BellaClientProvider provider, ContextService context, IOutputWriter output)
    : AsyncCommand<ConfigurePkiCaSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext ctx, ConfigurePkiCaSettings settings, CancellationToken ct)
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

            PkiCaResponse? result = null;
            await AnsiConsole.Status().StartAsync($"Configuring PKI CA for {envName}...", async _ =>
            {
                result = await client.Api.V1.Projects[projectSlug].Environments[envSlug].Pki.Ca.PostAsync(
                    new PkiCaSetupRequest
                    {
                        CommonName = settings.CommonName,
                        Organization = settings.Organization,
                        Country = settings.Country,
                        KeyType = settings.KeyType,
                        KeyBits = settings.KeyBits,
                        Ttl = settings.Ttl
                    },
                    cancellationToken: ct);
            });

            if (result is null)
            {
                output.WriteError("No response from server.");
                return 1;
            }

            output.WriteSuccess(result.Message ?? "PKI CA configured successfully.");

            if (!string.IsNullOrWhiteSpace(result.Certificate))
            {
                AnsiConsole.MarkupLine("\n[dim]CA Certificate:[/]");
                AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(result.Certificate)}[/]");
            }

            if (!string.IsNullOrWhiteSpace(result.SerialNumber))
                AnsiConsole.MarkupLine($"[dim]Serial: {result.SerialNumber}[/]");

            AnsiConsole.MarkupLine("\n[dim]Run [bold]bella pki ca[/] to view the CA and ACME directory URL.[/]");

            return 0;
        }
        catch (InvalidOperationException ex)
        {
            output.WriteError(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to configure PKI CA: {ex.Message}");
            return 1;
        }
    }
}
