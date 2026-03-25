using BellaBaxter.Client;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Totp;

public class GetTotpCodeSettings : CommandSettings
{
    [CommandArgument(0, "<name>")]
    public string Name { get; init; } = "";

    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("-e|--environment <SLUG>")]
    public string? Environment { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }

    /// <summary>Print only the code value with no decoration (useful for scripting).</summary>
    [CommandOption("--quiet|-q")]
    public bool Quiet { get; init; }
}

public class GetTotpCodeCommand(BellaClientProvider provider, ContextService context, IOutputWriter output)
    : AsyncCommand<GetTotpCodeSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext ctx, GetTotpCodeSettings settings, CancellationToken ct)
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
            var (envSlug, _, _) = await context.ResolveEnvironmentAsync(settings.Environment, projectSlug, client, ct);

            var response = await client.Api.V1.Projects[projectSlug].Environments[envSlug]
                .Totp[settings.Name].Code.GetAsync(cancellationToken: ct);

            if (response is null)
            {
                output.WriteError($"TOTP key '{settings.Name}' not found.");
                return 1;
            }

            if (output is JsonOutputWriter)
            {
                output.WriteObject(new { name = response.Name, code = response.Code, periodSeconds = response.PeriodSeconds });
                return 0;
            }

            if (settings.Quiet)
            {
                AnsiConsole.WriteLine(response.Code ?? "");
                return 0;
            }

            AnsiConsole.MarkupLine($"[bold]{response.Name}[/]: [green]{response.Code}[/]  [dim](valid for {response.PeriodSeconds ?? 30}s)[/]");
            return 0;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to get TOTP code: {ex.Message}");
            return 1;
        }
    }
}
