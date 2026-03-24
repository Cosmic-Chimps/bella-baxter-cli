using BellaBaxter.Client;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Projects;

public class UpdateProjectSettings : CommandSettings
{
    [CommandArgument(0, "<identifier>")]
    public string Identifier { get; init; } = "";

    [CommandOption("-n|--name <NAME>")]
    public string? Name { get; init; }

    [CommandOption("-d|--description <DESC>")]
    public string? Description { get; init; }

    [CommandOption("--add-tag <TAG>")]
    [System.ComponentModel.Description("Add a tag (can be specified multiple times)")]
    public string[]? AddTags { get; init; }

    [CommandOption("--remove-tag <TAG>")]
    [System.ComponentModel.Description("Remove a tag (can be specified multiple times)")]
    public string[]? RemoveTags { get; init; }

    [CommandOption("--set-tags <TAGS>")]
    [System.ComponentModel.Description("Replace all tags with a comma-separated list (use empty string to clear all)")]
    public string? SetTags { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class UpdateProjectCommand(BellaClientProvider provider, IOutputWriter output)
    : AsyncCommand<UpdateProjectSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, UpdateProjectSettings settings, CancellationToken ct)
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
            // First, get the existing project
            var existing = await client.Api.V1.Projects[settings.Identifier].GetAsync(cancellationToken: ct);
            if (existing is null)
            {
                output.WriteError($"Project '{settings.Identifier}' not found.");
                return 1;
            }

            var name = settings.Name;
            var description = settings.Description;

            if (string.IsNullOrWhiteSpace(name) && !(Console.IsOutputRedirected || output is JsonOutputWriter))
                name = AnsiConsole.Ask("Name:", defaultValue: existing.Name ?? "");

            if (string.IsNullOrWhiteSpace(description) && !(Console.IsOutputRedirected || output is JsonOutputWriter))
                description = AnsiConsole.Ask("Description:", defaultValue: existing.Description ?? "");

            // Compute updated tags
            List<string>? updatedTags = null;
            bool hastagsChange = settings.SetTags != null || (settings.AddTags?.Length > 0) || (settings.RemoveTags?.Length > 0);

            if (hastagsChange)
            {
                if (settings.SetTags != null)
                {
                    // Replace all tags
                    updatedTags = string.IsNullOrWhiteSpace(settings.SetTags)
                        ? []
                        : [.. settings.SetTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
                }
                else
                {
                    // Merge: start with existing, add and/or remove
                    var current = new HashSet<string>(existing.Tags ?? [], StringComparer.OrdinalIgnoreCase);
                    if (settings.AddTags != null)
                        foreach (var t in settings.AddTags) current.Add(t.Trim().ToLowerInvariant());
                    if (settings.RemoveTags != null)
                        foreach (var t in settings.RemoveTags) current.Remove(t.Trim().ToLowerInvariant());
                    updatedTags = [.. current];
                }
            }

            BellaBaxter.Client.Models.GetProjectResponse? updated = null;
            await AnsiConsole.Status().StartAsync("Updating project...", async _ =>
            {
                updated = await client.Api.V1.Projects[settings.Identifier].PutAsync(
                    new BellaBaxter.Client.Models.UpdateProjectRequest
                    {
                        Name = string.IsNullOrWhiteSpace(name) ? existing.Name : name,
                        Description = string.IsNullOrWhiteSpace(description) ? existing.Description : description,
                        Tags = updatedTags
                    }, cancellationToken: ct);
            });

            output.WriteSuccess($"Project '{updated?.Name ?? settings.Identifier}' updated.");

            if (updated?.Tags != null && updated.Tags.Count > 0)
                output.WriteInfo($"Tags: {string.Join(", ", updated.Tags)}");

            return 0;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to update project: {ex.Message}");
            return 1;
        }
    }
}
