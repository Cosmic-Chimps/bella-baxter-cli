using System.Text.RegularExpressions;
using BellaBaxter.Client;
using BellaBaxter.Client.Models;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Secrets;

public class PushSecretsSettings : CommandSettings
{
    [CommandOption("-i|--input <FILE>")]
    public string InputFile { get; init; } = "";

    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("-e|--environment <SLUG>")]
    public string? Environment { get; init; }

    [CommandOption("-d|--description <DESC>")]
    public string? Description { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }

    public override ValidationResult Validate()
    {
        if (string.IsNullOrWhiteSpace(InputFile))
            return ValidationResult.Error("--input is required.");
        if (!File.Exists(InputFile))
            return ValidationResult.Error($"File '{InputFile}' not found.");
        return ValidationResult.Success();
    }
}

public class PushSecretsCommand(
    BellaClientProvider provider,
    ContextService context,
    IOutputWriter output
) : AsyncCommand<PushSecretsSettings>
{
    private static readonly Regex KeyPattern = new(@"^[A-Z][A-Z0-9_]*$", RegexOptions.Compiled);
    private static readonly Regex CommentPattern = new(@"^\s*#", RegexOptions.Compiled);

    private static Dictionary<string, string> ParseEnvFile(string path)
    {
        var result = new Dictionary<string, string>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line) || CommentPattern.IsMatch(line))
                continue;
            var idx = line.IndexOf('=');
            if (idx < 0)
                continue;
            var key = line[..idx].Trim();
            var val = line[(idx + 1)..];
            // Skip non-uppercase keys
            if (!KeyPattern.IsMatch(key))
                continue;
            // Remove surrounding quotes if present
            if (
                val.Length >= 2
                && (
                    (val.StartsWith('"') && val.EndsWith('"'))
                    || (val.StartsWith('\'') && val.EndsWith('\''))
                )
            )
                val = val[1..^1];
            result[key] = val;
        }
        return result;
    }

    public override async Task<int> ExecuteAsync(
        CommandContext ctx,
        PushSecretsSettings settings,
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
            var (projectSlug, _, _, envSlug, envName, _) =
                await context.ResolveProjectEnvironmentAsync(
                    settings.Project,
                    settings.Environment,
                    client,
                    ct,
                    strictJwtLocal: true,
                    bootstrapBellaFromExplicit: true
                );

            var secrets = ParseEnvFile(settings.InputFile);
            if (secrets.Count == 0)
            {
                output.WriteWarning("No valid secrets found in the file.");
                return 0;
            }

            var providers = await client
                .Api.V1.Projects[projectSlug]
                .Environments[envSlug]
                .Providers.GetAsync(cancellationToken: ct);
            var providerList = providers ?? [];
            if (providerList.Count == 0)
            {
                output.WriteError(
                    "No providers assigned to this environment. Assign a provider first."
                );
                return 1;
            }

            var providerSlug = providerList[0].ProviderSlug ?? providerList[0].ProviderId ?? "";

            output.WriteInfo($"Pushing {secrets.Count} secrets to {envName}...");

            var success = 0;
            var failed = 0;

            await AnsiConsole
                .Progress()
                .StartAsync(async progressCtx =>
                {
                    var task = progressCtx.AddTask($"Uploading secrets", maxValue: secrets.Count);
                    foreach (var kvp in secrets)
                    {
                        try
                        {
                            try
                            {
                                await client
                                    .Api.V1.Projects[projectSlug]
                                    .Environments[envSlug]
                                    .Providers[providerSlug]
                                    .Secrets[kvp.Key]
                                    .PutAsync(
                                        new UpdateSecretRequest
                                        {
                                            Value = kvp.Value,
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
                                            Key = kvp.Key,
                                            Value = kvp.Value,
                                            Description = settings.Description,
                                        },
                                        cancellationToken: ct
                                    );
                            }
                            success++;
                        }
                        catch
                        {
                            failed++;
                        }
                        task.Increment(1);
                    }
                });

            if (failed == 0)
                output.WriteSuccess($"Pushed {success} secrets successfully.");
            else
                output.WriteWarning($"Pushed {success} secrets, {failed} failed.");

            return failed > 0 ? 1 : 0;
        }
        catch (InvalidOperationException ex)
        {
            output.WriteError(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to push secrets: {ex.Message}");
            return 1;
        }
    }
}
