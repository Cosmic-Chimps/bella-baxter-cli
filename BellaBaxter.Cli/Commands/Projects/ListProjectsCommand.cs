using BellaBaxter.Client;
using BellaBaxter.Client.Models;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace BellaCli.Commands.Projects;

public class ListProjectsSettings : CommandSettings
{
    [CommandOption("--page")]
    [DefaultValue(0)]
    public int Page { get; init; } = 0;

    [CommandOption("--size")]
    [DefaultValue(10)]
    public int Size { get; init; } = 10;

    [CommandOption("--sort-by")]
    [DefaultValue("createdAt")]
    public string SortBy { get; init; } = "createdAt";

    [CommandOption("--sort-dir")]
    [DefaultValue("desc")]
    public string SortDir { get; init; } = "desc";

    [CommandOption("--tag <TAG>")]
    [Description("Filter projects by tag")]
    public string? Tag { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class ListProjectsCommand(BellaClientProvider provider, IOutputWriter output)
    : AsyncCommand<ListProjectsSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ListProjectsSettings settings, CancellationToken ct)
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
            PageProjectResponse? page = null;
            await AnsiConsole.Status().StartAsync("Loading projects...", async _ =>
            {
                page = await client.Api.V1.Projects.GetAsync(q =>
                {
                    q.QueryParameters.Page = settings.Page;
                    q.QueryParameters.Size = settings.Size;
                    q.QueryParameters.SortBy = settings.SortBy;
                    q.QueryParameters.SortDir = settings.SortDir;
                    if (!string.IsNullOrWhiteSpace(settings.Tag))
                        q.QueryParameters.Tag = settings.Tag;
                }, cancellationToken: ct);
            });

            var projects = page?.Content ?? [];
            if (projects.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(settings.Tag))
                    output.WriteInfo($"No projects found with tag '{settings.Tag}'.");
                else
                    output.WriteInfo("No projects found.");
                return 0;
            }

            // Build table headers and rows; include Tags column only when at least one project has tags
            var hasTags = projects.Any(p => p.Tags != null && p.Tags.Count > 0);
            if (hasTags)
            {
                output.WriteTable(
                    ["Name", "ID", "Slug", "Tags", "Created"],
                    projects.Select(p => new[]
                    {
                        p.Name ?? "",
                        p.Id ?? "",
                        p.Slug ?? "",
                        p.Tags != null && p.Tags.Count > 0 ? string.Join(", ", p.Tags) : "",
                        p.CreatedAt?.ToString() ?? ""
                    }));
            }
            else
            {
                output.WriteTable(
                    ["Name", "ID", "Slug", "Description", "Created"],
                    projects.Select(p => new[]
                    {
                        p.Name ?? "",
                        p.Id ?? "",
                        p.Slug ?? "",
                        p.Description ?? "",
                        p.CreatedAt?.ToString() ?? ""
                    }));
            }

            var tagSuffix = !string.IsNullOrWhiteSpace(settings.Tag) ? $" (tag: {settings.Tag})" : "";
            output.WriteInfo($"Page {settings.Page + 1} · {projects.Count} of {page?.TotalElements ?? 0} projects{tagSuffix}");
            return 0;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to list projects: {ex.Message}");
            return 1;
        }
    }
}
