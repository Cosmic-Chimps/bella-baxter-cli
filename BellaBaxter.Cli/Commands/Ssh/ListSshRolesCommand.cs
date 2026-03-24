using BellaBaxter.Client;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Ssh;

public class ListSshRolesSettings : CommandSettings
{
    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("-e|--environment <SLUG>")]
    public string? Environment { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class ListSshRolesCommand(BellaClientProvider provider, ContextService context, IOutputWriter output)
    : AsyncCommand<ListSshRolesSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext ctx, ListSshRolesSettings settings, CancellationToken ct)
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

            BellaBaxter.Client.Models.SshRolesResponse? result = null;
            await AnsiConsole.Status().StartAsync($"Fetching SSH roles for {envName}...", async _ =>
            {
                result = await client.Api.V1.Projects[projectSlug].Environments[envSlug].Ssh.Roles.GetAsync(cancellationToken: ct);
            });

            var roles = result?.Roles ?? [];
            if (roles.Count == 0)
            {
                output.WriteInfo($"No SSH roles found in environment '{envName}'.");
                return 0;
            }

            output.WriteTable(
                ["Name", "Allowed Users", "Default TTL", "Max TTL"],
                roles.Select(r => new[]
                {
                    r.Name ?? "",
                    r.AllowedUsers ?? "",
                    r.DefaultTtl ?? "",
                    r.MaxTtl ?? ""
                }));

            return 0;
        }
        catch (InvalidOperationException ex)
        {
            output.WriteError(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to list SSH roles: {ex.Message}");
            return 1;
        }
    }
}
