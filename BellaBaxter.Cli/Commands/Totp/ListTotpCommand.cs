using BellaBaxter.Client;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Totp;

public class ListTotpSettings : CommandSettings
{
    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("-e|--environment <SLUG>")]
    public string? Environment { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class ListTotpCommand(BellaClientProvider provider, ContextService context, IOutputWriter output)
    : AsyncCommand<ListTotpSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext ctx, ListTotpSettings settings, CancellationToken ct)
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

            var keys = await client.Api.V1.Projects[projectSlug].Environments[envSlug].Totp.GetAsync(cancellationToken: ct);
            var list = keys ?? [];

            if (list.Count == 0)
            {
                output.WriteInfo($"No TOTP keys found in {envName}.");
                return 0;
            }

            if (output is JsonOutputWriter)
            {
                output.WriteList(list.Select(k => new
                {
                    name = k.Name,
                    issuer = k.Issuer,
                    accountName = k.AccountName,
                    algorithm = k.Algorithm,
                    digits = k.Digits,
                    period = k.Period
                }));
                return 0;
            }

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("[bold]Name[/]");
            table.AddColumn("[bold]Issuer[/]");
            table.AddColumn("[bold]Account[/]");
            table.AddColumn("[bold]Algorithm[/]");
            table.AddColumn("[bold]Digits[/]");
            table.AddColumn("[bold]Period[/]");

            foreach (var k in list)
            {
                table.AddRow(
                    k.Name ?? "",
                    k.Issuer ?? "-",
                    k.AccountName ?? "-",
                    k.Algorithm ?? "SHA1",
                    (k.Digits ?? 6).ToString(),
                    $"{k.Period ?? 30}s"
                );
            }

            AnsiConsole.Write(table);
            return 0;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to list TOTP keys: {ex.Message}");
            return 1;
        }
    }
}
