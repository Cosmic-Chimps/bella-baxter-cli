using BellaBaxter.Client;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Environments;

public class RemoveProviderFromEnvironmentSettings : CommandSettings
{
    [CommandArgument(0, "[env]")]
    public string? Environment { get; init; }

    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("--provider <SLUG>")]
    public string? ProviderSlug { get; init; }

    [CommandOption("-f|--force")]
    public bool Force { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class RemoveProviderFromEnvironmentCommand(
    BellaClientProvider provider,
    ContextService context,
    IOutputWriter output
) : AsyncCommand<RemoveProviderFromEnvironmentSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext ctx,
        RemoveProviderFromEnvironmentSettings settings,
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

        try
        {
            var (projectSlug, _, _) = await context.ResolveProjectAsync(
                settings.Project,
                client,
                ct
            );
            var (envSlug, envName, _) = await context.ResolveEnvironmentAsync(
                settings.Environment,
                projectSlug,
                client,
                ct
            );

            var isNonInteractive = Console.IsOutputRedirected || output is JsonOutputWriter;

            // Resolve provider slug interactively if not provided
            var providerSlug = settings.ProviderSlug;
            if (string.IsNullOrWhiteSpace(providerSlug))
            {
                if (isNonInteractive)
                {
                    output.WriteError("--provider is required in non-interactive mode.");
                    return 1;
                }

                var assigned =
                    await client
                        .Api.V1.Projects[projectSlug]
                        .Environments[envSlug]
                        .Providers.GetAsync(cancellationToken: ct) ?? [];

                if (assigned.Count == 0)
                {
                    output.WriteError($"Environment '{envName}' has no providers assigned.");
                    return 1;
                }

                providerSlug = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("Select provider to remove:")
                        .AddChoices(assigned.Select(p => p.ProviderSlug ?? p.ProviderName ?? ""))
                );
            }

            if (!settings.Force)
            {
                if (isNonInteractive)
                {
                    output.WriteError("Use --force to remove without confirmation.");
                    return 1;
                }

                var confirm = AnsiConsole.Confirm(
                    $"Remove provider [bold]{providerSlug}[/] from environment [bold]{envName}[/]?",
                    defaultValue: false
                );
                if (!confirm)
                {
                    output.WriteInfo("Cancelled.");
                    return 0;
                }
            }

            await AnsiConsole
                .Status()
                .StartAsync(
                    "Removing provider from environment...",
                    async _ =>
                    {
                        await client
                            .Api.V1.Projects[projectSlug]
                            .Environments[envSlug]
                            .Providers[providerSlug]
                            .DeleteAsync(cancellationToken: ct);
                    }
                );

            output.WriteSuccess($"Provider '{providerSlug}' removed from environment '{envName}'.");
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            output.WriteError(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to remove provider from environment: {ex.Message}");
            return 1;
        }
    }
}
