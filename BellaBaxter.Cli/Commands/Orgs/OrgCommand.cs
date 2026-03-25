using BellaBaxter.Client;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Orgs;

// ─── bella org current ────────────────────────────────────────────────────────

public class OrgCurrentCommand(CredentialStore credentials, IOutputWriter output)
    : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings, CancellationToken ct)
    {
        var tokens = credentials.LoadTokens();
        if (tokens is null)
        {
            output.WriteError("Not logged in. Run 'bella login' to authenticate.", "unauthenticated");
            return 1;
        }

        if (output is HumanOutputWriter)
        {
            AnsiConsole.MarkupLine("[bold]Active Org[/]");
            AnsiConsole.MarkupLine(new string('─', 40));

            if (tokens.OrgName != null || tokens.OrgSlug != null)
            {
                if (tokens.OrgName != null)
                    AnsiConsole.MarkupLine($"[white]Name:[/]  [cyan]{Markup.Escape(tokens.OrgName)}[/]");
                if (tokens.OrgSlug != null)
                    AnsiConsole.MarkupLine($"[white]Slug:[/]  [cyan]{Markup.Escape(tokens.OrgSlug)}[/]");
                if (tokens.OrgId != null)
                    AnsiConsole.MarkupLine($"[white]ID:[/]    [dim]{Markup.Escape(tokens.OrgId)}[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]No org context. Run 'bella login' to refresh.[/]");
            }
        }
        else
        {
            output.WriteObject(new
            {
                orgId = tokens.OrgId,
                orgName = tokens.OrgName,
                orgSlug = tokens.OrgSlug,
            });
        }

        return 0;
    }
}

// ─── bella org list ───────────────────────────────────────────────────────────

public class OrgListCommand(
    BellaClientProvider provider,
    CredentialStore credentials,
    IOutputWriter output
) : AsyncCommand<EmptyCommandSettings>
{
    public override async Task<int> ExecuteAsync(
        CommandContext context,
        EmptyCommandSettings settings,
        CancellationToken ct
    )
    {
        BellaClient client;
        try { client = provider.CreateClient(); }
        catch (InvalidOperationException)
        {
            output.WriteError("Not logged in. Run 'bella login' first.", "unauthenticated");
            return 1;
        }

        List<BellaBaxter.Client.Models.TenantAccess>? orgs = null;
        await AnsiConsole.Status().StartAsync("Fetching orgs...", async _ =>
        {
            orgs = await client.Api.Tenants.MyTenants.GetAsync(cancellationToken: ct);
        });

        var currentOrgId = credentials.LoadTokens()?.OrgId;

        if (output is HumanOutputWriter)
        {
            var table = new Table()
                .BorderColor(Color.Grey)
                .AddColumn("[bold]Name[/]")
                .AddColumn("[bold]Slug[/]")
                .AddColumn("[bold]Role[/]")
                .AddColumn("[bold]Active[/]");

            foreach (var org in orgs ?? [])
            {
                var isActive = org.TenantId.HasValue
                    && string.Equals(org.TenantId.Value.ToString(), currentOrgId, StringComparison.OrdinalIgnoreCase);
                table.AddRow(
                    Markup.Escape(org.TenantName ?? "—"),
                    Markup.Escape(org.Slug ?? "—"),
                    Markup.Escape(org.Role?.ToString() ?? "—"),
                    isActive ? "[green]✓[/]" : ""
                );
            }

            AnsiConsole.MarkupLine("[bold]Organizations[/]");
            AnsiConsole.Write(table);
        }
        else
        {
            output.WriteObject(orgs?.Select(o => new
            {
                tenantId = o.TenantId,
                name = o.TenantName,
                slug = o.Slug,
                role = o.Role?.ToString(),
                active = o.TenantId.HasValue
                    && string.Equals(o.TenantId.Value.ToString(), currentOrgId, StringComparison.OrdinalIgnoreCase),
            }));
        }

        return 0;
    }
}

// ─── bella org switch ─────────────────────────────────────────────────────────

public class OrgSwitchSettings : CommandSettings
{
    [CommandArgument(0, "<slug-or-id>")]
    [System.ComponentModel.Description("The org slug or ID to switch to")]
    public string SlugOrId { get; init; } = string.Empty;
}

public class OrgSwitchCommand(
    BellaClientProvider provider,
    AuthService authService,
    CredentialStore credentials,
    IOutputWriter output
) : AsyncCommand<OrgSwitchSettings>
{
    public override async Task<int> ExecuteAsync(
        CommandContext context,
        OrgSwitchSettings settings,
        CancellationToken ct
    )
    {
        BellaClient client;
        try { client = provider.CreateClient(); }
        catch (InvalidOperationException)
        {
            output.WriteError("Not logged in. Run 'bella login' first.", "unauthenticated");
            return 1;
        }

        // Resolve slug/id to tenant ID
        List<BellaBaxter.Client.Models.TenantAccess>? orgs = null;
        await AnsiConsole.Status().StartAsync("Fetching orgs...", async _ =>
        {
            orgs = await client.Api.Tenants.MyTenants.GetAsync(cancellationToken: ct);
        });

        var target = orgs?.FirstOrDefault(o =>
            string.Equals(o.Slug, settings.SlugOrId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(o.TenantId?.ToString(), settings.SlugOrId, StringComparison.OrdinalIgnoreCase));

        if (target is null)
        {
            output.WriteError(
                $"Org '{settings.SlugOrId}' not found. Run 'bella org list' to see available orgs."
            );
            return 1;
        }

        // Call switch endpoint
        BellaBaxter.Client.Models.SwitchTenantResponse? switchResponse = null;
        await AnsiConsole.Status().StartAsync($"Switching to org '{target.TenantName}'...", async _ =>
        {
            switchResponse = await client.Api.Tenants[target.TenantId!.Value.ToString()].Switch.PostAsync(cancellationToken: ct);
        });

        // Refresh token to get new JWT with updated tenant claims
        await AnsiConsole.Status().StartAsync("Refreshing token...", async _ =>
        {
            await authService.RefreshAsync(ct);
        });

        // The refreshed tokens now have the new org claims extracted by ToStoredTokens()
        var newTokens = credentials.LoadTokens();

        if (output is HumanOutputWriter)
        {
            AnsiConsole.MarkupLine($"[green]✓[/] Switched to org: [bold]{Markup.Escape(newTokens?.OrgName ?? target.TenantName ?? "")}[/] [dim]({Markup.Escape(newTokens?.OrgSlug ?? target.Slug ?? "")})[/]");

            var newOrgSlug = newTokens?.OrgSlug;
            if (newOrgSlug is not null)
            {
                var bellaFile = KeyContextService.FindBellaFile(Directory.GetCurrentDirectory());
                if (bellaFile is not null)
                {
                    KeyContextService.UpdateBellaOrg(bellaFile, newOrgSlug);
                    AnsiConsole.MarkupLine($"[dim]↺ Updated [cyan].bella[/] org → [cyan]{Markup.Escape(newOrgSlug)}[/][/]");
                }
                else
                {
                    AnsiConsole.MarkupLine("[dim]Run 'bella context init' to update your .bella file with the new org.[/]");
                }
            }
        }
        else
        {
            output.WriteObject(new
            {
                orgId = newTokens?.OrgId,
                orgName = newTokens?.OrgName,
                orgSlug = newTokens?.OrgSlug,
                switched = true,
            });
        }

        return 0;
    }
}
