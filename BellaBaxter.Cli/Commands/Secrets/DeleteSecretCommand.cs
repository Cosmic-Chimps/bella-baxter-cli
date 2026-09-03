using BellaBaxter.Client;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Secrets;

public class DeleteSecretSettings : CommandSettings
{
    [CommandArgument(0, "<key>")]
    public string Key { get; init; } = "";

    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("-e|--env|--environment <SLUG>")]
    public string? Environment { get; init; }

    [CommandOption("-f|--force")]
    public bool Force { get; init; }

    /// <summary>spec 020 (US4): name the secrets provider when several are attached.</summary>
    [CommandOption("--provider <SLUG>")]
    public string? Provider { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class DeleteSecretCommand(
    BellaClientProvider provider,
    ContextService context,
    IOutputWriter output,
    SecretProviderResolver providerResolver
) : AsyncCommand<DeleteSecretSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext ctx,
        DeleteSecretSettings settings,
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

            if (!settings.Force)
            {
                if (Console.IsOutputRedirected || output is JsonOutputWriter)
                {
                    output.WriteError("Use --force to delete without confirmation.");
                    return 1;
                }
                var confirm = AnsiConsole.Confirm(
                    $"Delete secret [bold]{settings.Key}[/]?",
                    defaultValue: false
                );
                if (!confirm)
                {
                    output.WriteInfo("Cancelled.");
                    return 0;
                }
            }

            // spec 020 (US4): resolve the destination by meaning, never by list position.
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
                    $"Deleting secret {settings.Key}...",
                    async _ =>
                    {
                        await client
                            .Api.V1.Projects[projectSlug]
                            .Environments[envSlug]
                            .Providers[providerSlug]
                            .Secrets[settings.Key]
                            .DeleteAsync(cancellationToken: ct);
                    }
                );

            output.WriteSuccess($"Secret '{settings.Key}' deleted.");
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            output.WriteError(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to delete secret: {ex.Message}");
            return 1;
        }
    }
}
