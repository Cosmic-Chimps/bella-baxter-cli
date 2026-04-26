using BellaBaxter.Client;
using BellaBaxter.Client.Models;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Pki;

public class ListPkiRolesSettings : CommandSettings
{
    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("-e|--environment <SLUG>")]
    public string? Environment { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class ListPkiRolesCommand(BellaClientProvider provider, ContextService context, IOutputWriter output)
    : AsyncCommand<ListPkiRolesSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext ctx, ListPkiRolesSettings settings, CancellationToken ct)
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

            PkiRolesResponse? result = null;
            await AnsiConsole.Status().StartAsync($"Fetching PKI roles for {envName}...", async _ =>
            {
                result = await client.Api.V1.Projects[projectSlug].Environments[envSlug].Pki.Roles.GetAsync(cancellationToken: ct);
            });

            var roles = result?.Roles ?? [];
            if (roles.Count == 0)
            {
                AnsiConsole.MarkupLine("[dim]No PKI roles found. Create one with 'bella pki roles create'.[/]");
                return 0;
            }

            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("[bold]Name[/]")
                .AddColumn("[bold]Allowed Domains[/]")
                .AddColumn("[bold]Allow Subdomains[/]")
                .AddColumn("[bold]Max TTL[/]")
                .AddColumn("[bold]Default TTL[/]");

            foreach (var role in roles)
            {
                table.AddRow(
                    Markup.Escape(role.Name ?? ""),
                    Markup.Escape(role.AllowedDomains ?? "*"),
                    role.AllowSubdomains == true ? "[green]yes[/]" : "[dim]no[/]",
                    Markup.Escape(role.MaxTtl ?? "-"),
                    Markup.Escape(role.DefaultTtl ?? "-")
                );
            }

            AnsiConsole.Write(table);
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            output.WriteError(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to list PKI roles: {ex.Message}");
            return 1;
        }
    }
}
