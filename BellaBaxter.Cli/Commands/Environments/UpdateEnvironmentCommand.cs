using BellaBaxter.Client;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Environments;

public class UpdateEnvironmentSettings : CommandSettings
{
    [CommandArgument(0, "[slug]")]
    public string? Slug { get; init; }

    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("-n|--name <NAME>")]
    public string? Name { get; init; }

    [CommandOption("-d|--description <DESC>")]
    public string? Description { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class UpdateEnvironmentCommand(BellaClientProvider provider, ContextService context, IOutputWriter output)
    : AsyncCommand<UpdateEnvironmentSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext ctx, UpdateEnvironmentSettings settings, CancellationToken ct)
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
            var (envSlug, _, _) = await context.ResolveEnvironmentAsync(settings.Slug, projectSlug, client, ct);

            var existing = await client.Api.V1.Projects[projectSlug].Environments[envSlug].GetAsync(cancellationToken: ct);

            var name = settings.Name;
            var description = settings.Description;

            if (string.IsNullOrWhiteSpace(name) && !(Console.IsOutputRedirected || output is JsonOutputWriter))
                name = AnsiConsole.Ask("Name:", defaultValue: existing?.Name ?? "");

            if (string.IsNullOrWhiteSpace(description) && !(Console.IsOutputRedirected || output is JsonOutputWriter))
                description = AnsiConsole.Ask("Description:", defaultValue: existing?.Description ?? "");

            await AnsiConsole.Status().StartAsync("Updating environment...", async _ =>
            {
                await client.Api.V1.Projects[projectSlug].Environments[envSlug].PutAsync(
                    new BellaBaxter.Client.Models.UpdateEnvironmentCommand
                    {
                        Name = string.IsNullOrWhiteSpace(name) ? existing?.Name : name,
                        Description = string.IsNullOrWhiteSpace(description) ? existing?.Description : description
                    }, cancellationToken: ct);
            });

            output.WriteSuccess($"Environment '{envSlug}' updated.");
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            output.WriteError(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to update environment: {ex.Message}");
            return 1;
        }
    }
}
