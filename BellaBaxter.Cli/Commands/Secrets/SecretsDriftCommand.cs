using BellaBaxter.Client.Models;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Secrets;

public class SecretsDriftSettings : CommandSettings
{
    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class SecretsDriftCommand(
    BellaClientProvider provider,
    ContextService context,
    IOutputWriter output
) : AsyncCommand<SecretsDriftSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext ctx,
        SecretsDriftSettings settings,
        CancellationToken ct
    )
    {
        provider.ApplyOutputModeOverrides(settings.Json);

        var client = provider.CreateClient();

        var (projectSlug, _, _) = await context.ResolveProjectAsync(settings.Project, client, ct);

        DriftResponse? drift = null;
        await AnsiConsole
            .Status()
            .StartAsync(
                $"Fetching drift for {projectSlug}...",
                async _ =>
                {
                    drift = await client
                        .Api.V1.Projects[projectSlug]
                        .SecretDrift.GetAsync(cancellationToken: ct);
                }
            );

        if (drift is null)
        {
            output.WriteError("No drift data returned.");
            return 1;
        }

        if (settings.Json)
        {
            output.WriteObject(drift);
            return 0;
        }

        RenderTable(drift, projectSlug);
        return drift.Summary?.DriftedKeys > 0 ? 1 : 0;
    }

    private static void RenderTable(DriftResponse drift, string projectSlug)
    {
        var envs = drift.Environments ?? [];
        var secrets = drift.Secrets ?? [];
        var summary = drift.Summary;

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title($"[bold]Secret Drift — {Markup.Escape(projectSlug)}[/]")
            .AddColumn(new TableColumn("[bold]KEY[/]").LeftAligned());

        foreach (var env in envs)
            table.AddColumn(new TableColumn($"[bold cyan]{Markup.Escape(env)}[/]").Centered());

        foreach (var item in secrets)
        {
            var key = item.Key ?? "";

            string keyMarkup;
            if (item.IsGlobal == true)
                keyMarkup = $"[blue]{Markup.Escape(key)}[/] [dim]🌐[/]";
            else if (item.EnvSpecific == true)
                keyMarkup = $"[dim]{Markup.Escape(key)} ~[/]";
            else if (item.MissingIn?.Count > 0)
                keyMarkup = $"[yellow]{Markup.Escape(key)}[/]";
            else
                keyMarkup = Markup.Escape(key);

            var cells = envs.Select(env =>
                {
                    if (item.IsGlobal == true)
                    {
                        if (item.PresentIn?.Contains(env) == true)
                            return "[green]✓[/] [dim](override)[/]";
                        if (item.InheritedIn?.Contains(env) == true)
                            return "[blue]🌐[/]";
                        return "[blue]🌐[/]";
                    }

                    if (item.EnvSpecific == true)
                        return item.PresentIn?.Contains(env) == true ? "[dim]✓[/]" : "[dim]—[/]";

                    if (item.PresentIn?.Contains(env) == true)
                        return "[green]✓[/]";
                    if (item.MissingIn?.Contains(env) == true)
                        return "[red]✗[/]";

                    return "[dim]—[/]";
                })
                .ToArray();

            table.AddRow([keyMarkup, .. cells]);
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        if (summary is not null)
        {
            var hasDrift = (summary.DriftedKeys ?? 0) > 0;

            AnsiConsole.MarkupLine(
                $"[dim]Total keys:[/] {summary.TotalKeys}  "
                    + $"[dim]Drifted:[/] {(hasDrift ? $"[yellow]{summary.DriftedKeys}[/]" : $"[green]{summary.DriftedKeys}[/]")}  "
                    + $"[dim]Env-specific:[/] {summary.EnvSpecificKeys}  "
                    + $"[dim]Global:[/] {summary.GlobalKeys}"
            );

            if (hasDrift)
                AnsiConsole.MarkupLine(
                    "[yellow]⚠  Drift detected. Keys marked ✗ are missing in those environments.[/]"
                );
            else
                AnsiConsole.MarkupLine(
                    "[green]✓  No drift — all keys are consistent across environments.[/]"
                );
        }
    }
}
