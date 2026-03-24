using BellaBaxter.Client;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Projects;

public class CreateProjectSettings : CommandSettings
{
    [CommandOption("-n|--name <NAME>")]
    public string? Name { get; init; }

    [CommandOption("-d|--description <DESC>")]
    public string? Description { get; init; }

    [CommandOption("--tag <TAG>")]
    [System.ComponentModel.Description("Add a tag (can be specified multiple times)")]
    public string[]? Tags { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class CreateProjectCommand(
    BellaClientProvider provider,
    IOutputWriter output
) : AsyncCommand<CreateProjectSettings>
{
    public override async Task<int> ExecuteAsync(
        CommandContext context,
        CreateProjectSettings settings,
        CancellationToken ct
    )
    {
        provider.ApplyOutputModeOverrides(settings.Json);

        BellaClient client;
        try
        {
            client = provider.CreateClient();
        }
        catch (InvalidOperationException)
        {
            output.WriteError("Not logged in. Run 'bella login' first.");
            return 1;
        }

        var name = settings.Name;
        var description = settings.Description;

        if (string.IsNullOrWhiteSpace(name))
        {
            if (Console.IsOutputRedirected || output is JsonOutputWriter)
            {
                output.WriteError("--name is required in non-interactive mode.");
                return 1;
            }
            name = AnsiConsole.Ask<string>("Project name:");
        }

        if (
            string.IsNullOrWhiteSpace(description)
            && !(Console.IsOutputRedirected || output is JsonOutputWriter)
        )
            description = AnsiConsole.Ask<string>(
                "Description [grey](optional, press Enter to skip)[/]:",
                defaultValue: ""
            );

        try
        {
            await AnsiConsole
                .Status()
                .StartAsync(
                    "Creating project...",
                    async _ =>
                    {
                        await client.Api.V1.Projects.PostAsync(
                            new BellaBaxter.Client.Models.CreateProjectCommand
                            {
                                Name = name,
                                Description = string.IsNullOrWhiteSpace(description)
                                    ? null
                                    : description,
                                Tags = settings.Tags?.Length > 0
                                    ? [.. settings.Tags]
                                    : null,
                            },
                            cancellationToken: ct
                        );
                    }
                );

            // Fetch the newly created project to get slug/id
            var page = await client.Api.V1.Projects.GetAsync(
                q =>
                {
                    q.QueryParameters.Size = 50;
                    q.QueryParameters.SortBy = "createdAt";
                    q.QueryParameters.SortDir = "desc";
                },
                cancellationToken: ct
            );
            var created = page?.Content?.FirstOrDefault(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
            );

            if (created is not null)
            {
                output.WriteSuccess(
                    $"Project '{created.Name}' created (slug: {created.Slug}, id: {created.Id})"
                );
            }
            else
            {
                output.WriteSuccess($"Project '{name}' created successfully.");
            }

            return 0;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to create project: {ex.Message}");
            return 1;
        }
    }
}
