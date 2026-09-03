using BellaBaxter.Client;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Secrets;

public class RotateSecretSettings : CommandSettings
{
    [CommandArgument(0, "<key>")]
    public string Key { get; init; } = "";

    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("-e|--env|--environment <SLUG>")]
    public string? Environment { get; init; }

    [CommandOption("--provider <SLUG>")]
    public string? Provider { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class RotateSecretCommand(
    BellaClientProvider provider,
    ContextService context,
    IOutputWriter output,
    SecretProviderResolver providerResolver
) : AsyncCommand<RotateSecretSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext ctx,
        RotateSecretSettings settings,
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
            var (projectSlug, _, _, envSlug, _, _) = await context.ResolveProjectEnvironmentAsync(
                settings.Project,
                settings.Environment,
                client,
                ct,
                strictJwtLocal: true,
                bootstrapBellaFromExplicit: true
            );

            // spec 020 (US4): resolve the destination by meaning, never by list position. An
            // explicitly supplied --provider is now VERIFIED to store secrets rather than taken
            // on trust.
            var providerSlug = await providerResolver.ResolveAsync(
                client,
                projectSlug,
                envSlug,
                settings.Provider,
                ct
            );
            if (providerSlug is null)
            {
                return 1;
            }

            await AnsiConsole
                .Status()
                .StartAsync(
                    $"Triggering rotation for {settings.Key}...",
                    async _ =>
                    {
                        await client
                            .Api.V1.Projects[projectSlug]
                            .Environments[envSlug]
                            .Providers[providerSlug]
                            .Secrets[settings.Key]
                            .Rotate.PostAsync(cancellationToken: ct);
                    }
                );

            output.WriteSuccess(
                $"Rotation triggered for '{settings.Key}'. The new value will be applied asynchronously.");
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            output.WriteError(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to trigger rotation: {ex.Message}");
            return 1;
        }
    }
}
