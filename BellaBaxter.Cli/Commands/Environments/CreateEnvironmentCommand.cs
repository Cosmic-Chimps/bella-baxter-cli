using BellaBaxter.Client;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Environments;

public class CreateEnvironmentSettings : CommandSettings
{
    [CommandOption("-n|--name <NAME>")]
    public string? Name { get; init; }

    [CommandOption("-d|--description <DESC>")]
    public string? Description { get; init; }

    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class CreateEnvironmentCommand(BellaClientProvider provider, ContextService context, IOutputWriter output)
    : AsyncCommand<CreateEnvironmentSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext ctx, CreateEnvironmentSettings settings, CancellationToken ct)
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
            var (projectSlug, projectName, _) = await context.ResolveProjectAsync(settings.Project, client, ct);

            var name = settings.Name;
            var description = settings.Description;

            if (string.IsNullOrWhiteSpace(name))
            {
                if (Console.IsOutputRedirected || output is JsonOutputWriter)
                {
                    output.WriteError("--name is required in non-interactive mode.");
                    return 1;
                }
                name = AnsiConsole.Ask<string>("Environment name:");
            }

            if (string.IsNullOrWhiteSpace(description) && !(Console.IsOutputRedirected || output is JsonOutputWriter))
                description = AnsiConsole.Ask("Description:", defaultValue: "");

            await AnsiConsole.Status().StartAsync("Creating environment...", async _ =>
            {
                await client.Api.V1.Projects[projectSlug].Environments.PostAsync(
                    new BellaBaxter.Client.Models.CreateEnvironmentCommand
                    {
                        Name = name,
                        Description = string.IsNullOrWhiteSpace(description) ? null : description
                    }, cancellationToken: ct);
            });

            output.WriteSuccess($"Environment '{name}' created in project '{projectName}'.");
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            output.WriteError(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to create environment: {ex.Message}");
            return 1;
        }
    }
}
