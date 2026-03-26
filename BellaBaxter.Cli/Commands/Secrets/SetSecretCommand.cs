using System.Text.RegularExpressions;
using BellaBaxter.Client;
using BellaBaxter.Client.Models;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Secrets;

public class SetSecretSettings : CommandSettings
{
    [CommandArgument(0, "<key>")]
    public string Key { get; init; } = "";

    [CommandArgument(1, "[value]")]
    public string? Value { get; init; }

    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("-e|--environment <SLUG>")]
    public string? Environment { get; init; }

    [CommandOption("-d|--description <DESC>")]
    public string? Description { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class SetSecretCommand(
    BellaClientProvider provider,
    ContextService context,
    IOutputWriter output
) : AsyncCommand<SetSecretSettings>
{
    private static readonly Regex KeyPattern = new(@"^[A-Z][A-Z0-9_]*$", RegexOptions.Compiled);

    public override async Task<int> ExecuteAsync(
        CommandContext ctx,
        SetSecretSettings settings,
        CancellationToken ct
    )
    {
        provider.ApplyOutputModeOverrides(settings.Json);

        if (!KeyPattern.IsMatch(settings.Key))
        {
            output.WriteError($"Invalid key '{settings.Key}'. Keys must match ^[A-Z][A-Z0-9_]*$");
            return 1;
        }

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

            var value = settings.Value;
            if (string.IsNullOrEmpty(value))
            {
                if (Console.IsOutputRedirected || output is JsonOutputWriter)
                {
                    output.WriteError("Value is required in non-interactive mode.");
                    return 1;
                }
                value = AnsiConsole.Prompt(
                    new TextPrompt<string>($"Value for [bold]{settings.Key}[/]:").Secret()
                );
            }

            // Get the first provider for this environment
            var providers = await client
                .Api.V1.Projects[projectSlug]
                .Environments[envSlug]
                .Providers.GetAsync(cancellationToken: ct);
            var providerList = providers ?? [];
            if (providerList.Count == 0)
            {
                output.WriteError(
                    $"No providers assigned to this environment. Assign a provider first."
                );
                return 1;
            }

            var providerSlug = providerList[0].ProviderSlug ?? providerList[0].ProviderId ?? "";

            await AnsiConsole
                .Status()
                .StartAsync(
                    $"Setting secret {settings.Key}...",
                    async _ =>
                    {
                        // Try update first, then create
                        try
                        {
                            await client
                                .Api.V1.Projects[projectSlug]
                                .Environments[envSlug]
                                .Providers[providerSlug]
                                .Secrets[settings.Key]
                                .PutAsync(
                                    new UpdateSecretRequest
                                    {
                                        Value = value,
                                        Description = settings.Description,
                                    },
                                    cancellationToken: ct
                                );
                        }
                        catch
                        {
                            await client
                                .Api.V1.Projects[projectSlug]
                                .Environments[envSlug]
                                .Providers[providerSlug]
                                .Secrets.PostAsync(
                                    new CreateSecretRequest
                                    {
                                        Key = settings.Key,
                                        Value = value,
                                        Description = settings.Description,
                                    },
                                    cancellationToken: ct
                                );
                        }
                    }
                );

            output.WriteSuccess($"Secret '{settings.Key}' set successfully.");
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            output.WriteError(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to set secret: {ex.Message}");
            return 1;
        }
    }
}
