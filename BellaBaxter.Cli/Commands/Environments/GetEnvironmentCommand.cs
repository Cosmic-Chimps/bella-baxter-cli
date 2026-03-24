using BellaBaxter.Client;
using BellaBaxter.Client.Models;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Environments;

public class GetEnvironmentSettings : CommandSettings
{
    [CommandArgument(0, "[slug]")]
    public string? Slug { get; init; }

    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class GetEnvironmentCommand(BellaClientProvider provider, ContextService context, IOutputWriter output)
    : AsyncCommand<GetEnvironmentSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext ctx, GetEnvironmentSettings settings, CancellationToken ct)
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

            EnvironmentResponse? env = null;
            List<EnvironmentProviderResponse>? providers = null;

            await AnsiConsole.Status().StartAsync("Loading environment...", async _ =>
            {
                env = await client.Api.V1.Projects[projectSlug].Environments[envSlug].GetAsync(cancellationToken: ct);
                providers = await client.Api.V1.Projects[projectSlug].Environments[envSlug].Providers.GetAsync(cancellationToken: ct);
            });

            if (env is null)
            {
                output.WriteError($"Environment '{envSlug}' not found.");
                return 1;
            }

            output.WriteObject(new
            {
                id = env.Id,
                name = env.Name,
                slug = env.Slug,
                description = env.Description,
                projectId = env.ProjectId,
                projectName = env.ProjectName,
                providerCount = env.ProviderCount,
                secretCount = env.SecretCount,
                memberCount = env.MemberCount,
                currentUserRole = env.CurrentUserRole,
                createdAt = env.CreatedAt?.ToString(),
                updatedAt = env.UpdatedAt?.ToString()
            });

            if (providers?.Count > 0)
            {
                output.WriteInfo("Providers:");
                output.WriteTable(
                    ["Name", "Type", "ID"],
                    providers.Select(p => new[] { p.ProviderName ?? "", p.ProviderType ?? "", p.ProviderId ?? "" }));
            }

            return 0;
        }
        catch (InvalidOperationException ex)
        {
            output.WriteError(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to get environment: {ex.Message}");
            return 1;
        }
    }
}
