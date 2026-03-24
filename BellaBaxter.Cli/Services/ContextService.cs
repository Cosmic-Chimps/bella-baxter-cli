using BellaBaxter.Client;
using BellaBaxter.Client.Models;
using BellaCli.Commands.Shell;
using BellaCli.Infrastructure;
using Spectre.Console;

namespace BellaCli.Services;

public class ContextService(ConfigService config, IOutputWriter output, CredentialStore credentials, KeyContextService keyContext)
{
    public async Task<(string slug, string name, string id)> ResolveProjectAsync(
        string? projectArg,
        BellaClient client,
        CancellationToken ct
    )
    {
        // 1. Explicit arg — find by slug or id in project list
        if (!string.IsNullOrWhiteSpace(projectArg))
        {
            // Try fetching directly
            try
            {
                var p = await client.Api.V1.Projects[projectArg].GetAsync(cancellationToken: ct);
                if (p is not null)
                    return (
                        p.Slug ?? projectArg,
                        p.Name ?? projectArg,
                        p.Id?.ToString() ?? projectArg
                    );
            }
            catch
            { /* try fallback below */
            }
            // Fetch all and search
            var page = await client.Api.V1.Projects.GetAsync(q => q.QueryParameters.Size = 200, ct);
            var match = page?.Content?.FirstOrDefault(p =>
                string.Equals(p.Slug, projectArg, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.Id, projectArg, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.Name, projectArg, StringComparison.OrdinalIgnoreCase)
            );
            if (match is not null)
                return (match.Slug ?? projectArg, match.Name ?? projectArg, match.Id ?? projectArg);
            throw new InvalidOperationException($"Project '{projectArg}' not found.");
        }

        // 2. API key mode → context comes from the key itself (GET /api/v1/keys/me)
        if (credentials.LoadApiKey() is not null)
        {
            var ctx = await keyContext.DiscoverAsync(ct);
            if (ctx?.ProjectSlug is not null)
                return (ctx.ProjectSlug, ctx.ProjectName, ctx.ProjectSlug);
            throw new InvalidOperationException(
                "Could not resolve project from API key. Is the server reachable?"
            );
        }

        // 3. .bella file in current directory or any parent (directory-scoped, like .git)
        var (bellaProject, _, _) = ContextCommand.ResolveContext(config);
        if (bellaProject is not null)
        {
            try
            {
                var p = await client.Api.V1.Projects[bellaProject].GetAsync(cancellationToken: ct);
                if (p is not null)
                    return (p.Slug ?? bellaProject, p.Name ?? bellaProject, p.Id?.ToString() ?? bellaProject);
            }
            catch { /* fall through to interactive */ }
            return (bellaProject, bellaProject, bellaProject);
        }

        // 4. Interactive picker (human terminal only)
        if (Console.IsOutputRedirected || output is JsonOutputWriter)
            throw new InvalidOperationException(
                "No context found. Add a .bella file (run 'bella context init') or pass --project."
            );

        var projects = await client.Api.V1.Projects.GetAsync(q => q.QueryParameters.Size = 200, ct);
        var list = projects?.Content ?? [];
        if (list.Count == 0)
            throw new InvalidOperationException("No projects found. Create one first.");

        var chosen = AnsiConsole.Prompt(
            new SelectionPrompt<ProjectResponse>()
                .Title("[bold]Select a project:[/]")
                .UseConverter(p => $"{p.Name} [grey]({p.Slug})[/]")
                .AddChoices(list)
        );

        return (chosen.Slug!, chosen.Name!, chosen.Id!);
    }

    public async Task<(string slug, string name, string id)> ResolveEnvironmentAsync(
        string? envArg,
        string projectSlug,
        BellaClient client,
        CancellationToken ct
    )
    {
        // 1. Explicit arg
        if (!string.IsNullOrWhiteSpace(envArg))
        {
            try
            {
                var e = await client
                    .Api.V1.Projects[projectSlug]
                    .Environments[envArg]
                    .GetAsync(cancellationToken: ct);
                if (e is not null)
                    return (e.Slug ?? envArg, e.Name ?? envArg, e.Id ?? envArg);
            }
            catch
            { /* try list */
            }
            var envList = await client
                .Api.V1.Projects[projectSlug]
                .Environments.GetAsync(cancellationToken: ct);
            var match = envList?.FirstOrDefault(e =>
                string.Equals(e.Slug, envArg, StringComparison.OrdinalIgnoreCase)
                || string.Equals(e.Id, envArg, StringComparison.OrdinalIgnoreCase)
                || string.Equals(e.Name, envArg, StringComparison.OrdinalIgnoreCase)
            );
            if (match is not null)
                return (match.Slug ?? envArg, match.Name ?? envArg, match.Id ?? envArg);
            throw new InvalidOperationException(
                $"Environment '{envArg}' not found in project '{projectSlug}'."
            );
        }

        // 2. API key mode → environment from the key itself
        if (credentials.LoadApiKey() is not null)
        {
            var ctx = await keyContext.DiscoverAsync(ct);
            if (ctx?.EnvironmentSlug is not null)
                return (ctx.EnvironmentSlug, ctx.EnvironmentName ?? ctx.EnvironmentSlug, ctx.EnvironmentSlug);
            // Manager/Admin API keys have no env scope — let them pick
        }
        else
        {
            // 3. .bella file
            var (_, bellaEnv, _) = ContextCommand.ResolveContext(config);
            if (bellaEnv is not null)
                return (bellaEnv, bellaEnv, bellaEnv);
        }

        // 4. Interactive
        if (Console.IsOutputRedirected || output is JsonOutputWriter)
            throw new InvalidOperationException(
                "No environment context found. Add a .bella file (run 'bella context init') or pass --environment."
            );

        var envs = await client
            .Api.V1.Projects[projectSlug]
            .Environments.GetAsync(cancellationToken: ct);
        var envItems = envs ?? [];
        if (envItems.Count == 0)
            throw new InvalidOperationException(
                $"No environments found in project '{projectSlug}'."
            );

        var chosenEnv = AnsiConsole.Prompt(
            new SelectionPrompt<EnvironmentResponse>()
                .Title("[bold]Select an environment:[/]")
                .UseConverter(e => $"{e.Name} [grey]({e.Slug})[/]")
                .AddChoices(envItems)
        );

        return (chosenEnv.Slug!, chosenEnv.Name!, chosenEnv.Id!);
    }
}
