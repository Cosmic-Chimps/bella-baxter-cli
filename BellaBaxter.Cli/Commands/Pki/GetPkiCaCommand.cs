using BellaBaxter.Client;
using BellaBaxter.Client.Models;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Pki;

public class GetPkiCaSettings : CommandSettings
{
    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("-e|--env|--environment <SLUG>")]
    public string? Environment { get; init; }

    [CommandOption("--output <FILE>")]
    [System.ComponentModel.Description("Save CA certificate PEM to file")]
    public string? Output { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class GetPkiCaCommand(BellaClientProvider provider, ContextService context, IOutputWriter output)
    : AsyncCommand<GetPkiCaSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext ctx, GetPkiCaSettings settings, CancellationToken ct)
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

            PkiCaPublicKeyResponse? result = null;
            await AnsiConsole.Status().StartAsync($"Fetching PKI CA for {envName}...", async _ =>
            {
                result = await client.Api.V1.Projects[projectSlug].Environments[envSlug].Pki.Ca.GetAsync(cancellationToken: ct);
            });

            if (result is null)
            {
                output.WriteError("No CA configured for this environment. Run 'bella pki configure' first.");
                return 1;
            }

            if (!string.IsNullOrWhiteSpace(settings.Output))
            {
                var certPem = result.CaChain ?? result.Certificate ?? "";
                await File.WriteAllTextAsync(settings.Output, certPem, ct);
                AnsiConsole.MarkupLine($"[green]✓[/] CA certificate saved to [bold]{settings.Output}[/]");
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(result.Certificate))
                {
                    AnsiConsole.MarkupLine("[bold]CA Certificate:[/]");
                    AnsiConsole.MarkupLine(Markup.Escape(result.Certificate));
                }

                if (!string.IsNullOrWhiteSpace(result.CaChain))
                {
                    AnsiConsole.MarkupLine("\n[bold]CA Chain:[/]");
                    AnsiConsole.MarkupLine(Markup.Escape(result.CaChain));
                }
            }

            if (!string.IsNullOrWhiteSpace(result.AcmeDirectoryUrl))
            {
                AnsiConsole.MarkupLine($"\n[bold]ACME Directory URL:[/]");
                AnsiConsole.MarkupLine($"[cyan]{Markup.Escape(result.AcmeDirectoryUrl)}[/]");
                AnsiConsole.MarkupLine("\n[dim]Use this URL with certbot, acme.sh, or Caddy for automatic TLS:[/]");
                AnsiConsole.MarkupLine($"[dim]  certbot certonly --server {Markup.Escape(result.AcmeDirectoryUrl)} --standalone -d example.com[/]");
            }

            if (!string.IsNullOrWhiteSpace(result.Instructions))
                AnsiConsole.MarkupLine($"\n[dim]{Markup.Escape(result.Instructions)}[/]");

            return 0;
        }
        catch (InvalidOperationException ex)
        {
            output.WriteError(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to fetch PKI CA: {ex.Message}");
            return 1;
        }
    }
}
