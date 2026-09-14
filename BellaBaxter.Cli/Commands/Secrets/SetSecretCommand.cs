using System.Text.RegularExpressions;
using BellaBaxter.Client;
using BellaBaxter.Client.Models;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Microsoft.Kiota.Abstractions;
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

    [CommandOption("-e|--env|--environment <SLUG>")]
    public string? Environment { get; init; }

    [CommandOption("-d|--description <DESC>")]
    public string? Description { get; init; }

    /// <summary>spec 020 (US4): name the secrets provider to write to when several are attached.</summary>
    [CommandOption("--provider <SLUG>")]
    public string? Provider { get; init; }

    /// <summary>
    /// Issue #611: scope tags decide which secrets a scoped token (<c>bella issue --scope</c>) can
    /// read. Repeat per scope: <c>--scope drive --scope ci</c>.
    /// </summary>
    [CommandOption("--scope <NAME>")]
    public string[] Scopes { get; init; } = [];

    /// <summary>Any other tag, <c>KEY=VALUE</c>, repeatable. For scopes prefer <c>--scope</c>.</summary>
    [CommandOption("--tag <KEY=VALUE>")]
    public string[] Tags { get; init; } = [];

    /// <summary>
    /// The secret's declared type (String, Int, Bool, Uri, Json, Guid, Base64, Certificate).
    /// Deliberately NOT validated here: the set grows (spec 020 added Certificate), and a stale
    /// client-side list would refuse a type the server accepts. The server validates it.
    /// </summary>
    [CommandOption("--type <TYPE>")]
    public string? Type { get; init; }

    /// <summary>Exclude this secret from the security scanner. Explicit value so it can be turned back on.</summary>
    [CommandOption("--ignore-in-scan <true|false>")]
    public bool? IgnoreInScan { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }

    /// <summary>True when the caller asked for anything that lives on the metadata endpoint.</summary>
    public bool WantsMetadataWrite => Scopes.Length > 0 || Tags.Length > 0 || IgnoreInScan.HasValue;
}

public class SetSecretCommand(
    BellaClientProvider provider,
    ContextService context,
    IOutputWriter output,
    SecretProviderResolver providerResolver
) : AsyncCommand<SetSecretSettings>
{
    private static readonly Regex KeyPattern = new(
        @"^[A-Za-z][A-Za-z0-9_]*$",
        RegexOptions.Compiled
    );

    protected override async Task<int> ExecuteAsync(
        CommandContext ctx,
        SetSecretSettings settings,
        CancellationToken ct
    )
    {
        provider.ApplyOutputModeOverrides(settings.Json);

        if (!KeyPattern.IsMatch(settings.Key))
        {
            output.WriteError(
                $"Invalid key '{settings.Key}'. Keys must match ^[A-Za-z][A-Za-z0-9_]*$"
            );
            return 1;
        }

        // Issue #611: compose and validate the tag arguments BEFORE any network call, so a typo
        // costs nothing and never leaves a value written with the tags rejected.
        if (
            !SecretMetadataArguments.TryCompose(
                settings.Scopes,
                settings.Tags,
                out var requestedTags,
                out var tagError
            )
        )
        {
            output.WriteError(tagError!);
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
                if (!Interactivity.IsInteractive(output))
                {
                    output.WriteError("Value is required in non-interactive mode.");
                    return 1;
                }
                value = await AnsiConsole.PromptAsync(
                    new TextPrompt<string>($"Value for [bold]{settings.Key}[/]:")
                        .Secret()
                        .ClearOnFinish(),
                    ct
                );
            }

            // spec 020 (US4): resolve the destination by MEANING, never by list position —
            // an environment configured for certificate rotation also has non-secret providers
            // attached, and taking the first one could aim this write at any of them.
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

            var created = false;

            await output.StatusAsync($"Setting secret {settings.Key}...", async () =>
                    {
                        // Update first, then create. The catch is narrowed to "it isn't there yet":
                        // a bare catch here turned every failure — a 403, a validation error, a
                        // network fault — into a create attempt, and the operator was then shown the
                        // CREATE error, which describes the wrong problem.
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
                                        Type = settings.Type,
                                    },
                                    cancellationToken: ct
                                );
                        }
                        catch (Exception ex) when (IsNotFound(ex))
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
                                        Type = settings.Type,
                                    },
                                    cancellationToken: ct
                                );
                            created = true;
                        }
                    }
                );

            // Tags, scopes and the scan flag live on a DIFFERENT endpoint. The create/update
            // contract advertises `tags` and the server writes `Tags: null` on both paths
            // (CreateSecret.cs / UpdateSecret.cs) — only PATCH .../metadata emits
            // SecretTagsUpdated. Sending them above would have looked like it worked.
            if (settings.WantsMetadataWrite)
            {
                var applied = await ApplyMetadataAsync(
                    client,
                    projectSlug,
                    envSlug,
                    providerSlug,
                    settings,
                    requestedTags,
                    createdSecret: created,
                    ct
                );

                if (!applied)
                {
                    // The value IS written. Say so plainly rather than reporting a flat failure —
                    // an operator who re-runs the whole command otherwise writes the value twice
                    // while still not knowing which half failed.
                    output.WriteError(
                        $"The value of '{settings.Key}' was written, but its tags were not. "
                            + "Re-run the same command to retry the tags."
                    );
                    return 1;
                }
            }

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

    /// <summary>
    /// "The secret does not exist yet" — the only condition that may fall through to a create.
    /// Kiota surfaces a declared 404 as <c>ProblemDetails</c> and anything else as
    /// <c>ApiException</c>, so both are checked by status rather than by message.
    /// </summary>
    private static bool IsNotFound(Exception ex) =>
        ex switch
        {
            ProblemDetails problem => problem.ResponseStatusCode == 404,
            ApiException api => api.ResponseStatusCode == 404,
            _ => false,
        };

    /// <summary>
    /// Writes tags / scopes / the scan flag through <c>PATCH …/secrets/{key}/metadata</c> — the ONLY
    /// path that emits <c>SecretTagsUpdated</c>. The create/update contract no longer pretends
    /// otherwise: create persists tags, update refuses them (issue #613).
    ///
    /// <para>Because that endpoint REPLACES the whole tag dictionary, an existing secret's current
    /// tags are read first (<c>getSecretMetadata</c>, issue #614) and merged. If the read fails the
    /// write is refused rather than guessed: replacing tags we could not see would delete metadata on
    /// a transient error, and an unreadable state is never a verdict.</para>
    /// </summary>
    private async Task<bool> ApplyMetadataAsync(
        BellaClient client,
        string projectSlug,
        string envSlug,
        string providerSlug,
        SetSecretSettings settings,
        Dictionary<string, string> requestedTags,
        bool createdSecret,
        CancellationToken ct
    )
    {
        Dictionary<string, string>? tagsToWrite = null;

        if (requestedTags.Count > 0)
        {
            IReadOnlyDictionary<string, string>? existing = null;

            // A secret this command just created has no prior tags, so there is nothing to read.
            if (!createdSecret)
            {
                try
                {
                    var metadata = await client
                        .Api.V1.Projects[projectSlug]
                        .Environments[envSlug]
                        .Providers[providerSlug]
                        .Secrets[settings.Key]
                        .Metadata.GetAsync(cancellationToken: ct);

                    existing = metadata?.Tags?.AdditionalData?.ToDictionary(
                        kv => kv.Key,
                        kv => kv.Value?.ToString() ?? string.Empty
                    );
                }
                catch (Exception ex)
                {
                    output.WriteError(
                        "Could not read the secret's current tags, so its tags were left untouched "
                            + $"rather than replaced: {Describe(ex)}"
                    );
                    return false;
                }
            }

            tagsToWrite = SecretMetadataArguments.MergeOver(existing, requestedTags);
        }

        try
        {
            await client
                .Api.V1.Projects[projectSlug]
                .Environments[envSlug]
                .Providers[providerSlug]
                .Secrets[settings.Key]
                .Metadata.PatchAsync(
                    SecretMetadataArguments.BuildRequest(tagsToWrite, settings.IgnoreInScan),
                    cancellationToken: ct
                );
            return true;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to write secret metadata: {Describe(ex)}");
            return false;
        }
    }

    private static string Describe(Exception ex) =>
        ex switch
        {
            ProblemDetails problem =>
                $"{problem.Detail ?? problem.Title} (HTTP {problem.ResponseStatusCode})",
            ApiException api => $"HTTP {api.ResponseStatusCode}",
            _ => ex.Message,
        };
}
