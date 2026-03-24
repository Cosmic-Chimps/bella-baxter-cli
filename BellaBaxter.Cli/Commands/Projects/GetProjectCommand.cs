using BellaBaxter.Client;
using BellaBaxter.Client.Models;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Projects;

public class GetProjectSettings : CommandSettings
{
    [CommandArgument(0, "<identifier>")]
    public string Identifier { get; init; } = "";

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class GetProjectCommand(BellaClientProvider provider, IOutputWriter output)
    : AsyncCommand<GetProjectSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, GetProjectSettings settings, CancellationToken ct)
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
            GetProjectResponse? project = null;
            await AnsiConsole.Status().StartAsync("Loading project...", async _ =>
            {
                project = await client.Api.V1.Projects[settings.Identifier].GetAsync(cancellationToken: ct);
            });

            if (project is null)
            {
                output.WriteError($"Project '{settings.Identifier}' not found.");
                return 1;
            }

            output.WriteObject(new
            {
                id = project.Id,
                name = project.Name,
                slug = project.Slug,
                description = project.Description,
                status = project.Status,
                ownerName = project.OwnerName,
                currentUserRole = project.CurrentUserRole,
                createdAt = project.CreatedAt?.ToString(),
                updatedAt = project.UpdatedAt?.ToString()
            });

            return 0;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to get project: {ex.Message}");
            return 1;
        }
    }
}
