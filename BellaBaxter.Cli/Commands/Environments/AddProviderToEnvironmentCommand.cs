using BellaBaxter.Client;
using BellaBaxter.Client.Models;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Environments;

public class AddProviderToEnvironmentSettings : CommandSettings
{
    [CommandArgument(0, "[env]")]
    public string? Environment { get; init; }

    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("--provider <SLUG>")]
    public string? ProviderSlug { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class AddProviderToEnvironmentCommand(
    BellaClientProvider provider,
    ContextService context,
    IOutputWriter output
) : AsyncCommand<AddProviderToEnvironmentSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext ctx,
        AddProviderToEnvironmentSettings settings,
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

            // Resolve provider slug
            var providerSlug = settings.ProviderSlug;
            List<ProviderResponse>? allProviders = null;

            await AnsiConsole
                .Status()
                .StartAsync(
                    "Loading providers...",
                    async _ =>
                    {
                        allProviders = await client.Api.V1.Providers.GetAsync(
                            cancellationToken: ct
                        );
                    }
                );

            allProviders ??= [];

            if (string.IsNullOrWhiteSpace(providerSlug))
            {
                if (isNonInteractive)
                {
                    output.WriteError("--provider is required in non-interactive mode.");
                    return 1;
                }

                if (allProviders.Count == 0)
                {
                    output.WriteError(
                        "No providers available. Create a provider first with 'bella providers create'."
                    );
                    return 1;
                }

                providerSlug = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("Select provider to add:")
                        .AddChoices(allProviders.Select(p => p.Slug ?? p.Name ?? p.Id ?? ""))
                );
            }

            var resolved = allProviders.FirstOrDefault(p =>
                string.Equals(p.Slug, providerSlug, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.Name, providerSlug, StringComparison.OrdinalIgnoreCase)
            );

            if (resolved is null)
            {
                output.WriteError($"Provider '{providerSlug}' not found.");
                return 1;
            }

            if (!Guid.TryParse(resolved.Id, out var providerId))
            {
                output.WriteError($"Provider '{providerSlug}' has an invalid ID.");
                return 1;
            }

            await AnsiConsole
                .Status()
                .StartAsync(
                    "Adding provider to environment...",
                    async _ =>
                    {
                        await client
                            .Api.V1.Projects[projectSlug]
                            .Environments[envSlug]
                            .Providers.PostAsync(
                                new AssignProvidersCommand { ProviderIds = [providerId] },
                                cancellationToken: ct
                            );
                    }
                );

            output.WriteSuccess(
                $"Provider '{resolved.Name ?? providerSlug}' added to environment '{envName}'."
            );
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            output.WriteError(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to add provider to environment: {ex.Message}");
            return 1;
        }
    }
}
