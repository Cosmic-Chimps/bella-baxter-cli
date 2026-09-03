using BellaBaxter.Client;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Environments;

public class CreateEnvironmentSettings : CommandSettings
{
    [CommandOption("-n|--name <NAME>")]
    public string? Name { get; init; }

    [CommandOption("-d|--description <DESC>")]
    public string? Description { get; init; }

    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class CreateEnvironmentCommand(
    BellaClientProvider provider,
    ContextService context,
    IOutputWriter output
) : AsyncCommand<CreateEnvironmentSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext ctx,
        CreateEnvironmentSettings settings,
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
            var (projectSlug, projectName, _) = await context.ResolveProjectAsync(
                settings.Project,
                client,
                ct
            );

            var name = settings.Name;
            var description = settings.Description;

            if (string.IsNullOrWhiteSpace(name))
            {
                if (Console.IsOutputRedirected || output is JsonOutputWriter)
                {
                    output.WriteError("--name is required in non-interactive mode.");
                    return 1;
                }
                name = AnsiConsole.Ask<string>("Environment name:");
            }

            if (
                string.IsNullOrWhiteSpace(description)
                && !(Console.IsOutputRedirected || output is JsonOutputWriter)
            )
                description = AnsiConsole.Ask("Description:", defaultValue: "");

            BellaBaxter.Client.Models.EnvironmentOperationResponse? created = null;
            await AnsiConsole
                .Status()
                .StartAsync(
                    "Creating environment...",
                    async _ =>
                    {
                        created = await client
                            .Api.V1.Projects[projectSlug]
                            .Environments.PostAsync(
                                new BellaBaxter.Client.Models.CreateEnvironmentCommand
                                {
                                    Name = name,
                                    Description = string.IsNullOrWhiteSpace(description)
                                        ? null
                                        : description,
                                },
                                cancellationToken: ct
                            );
                    }
                );

            output.WriteSuccess($"Environment '{name}' created in project '{projectName}'.");

            // Spec 028 (FR-020a): a new environment starts in Strict. Say so, and say what that checks,
            // at the moment the operator can still choose otherwise for a development environment.
            // Kiota renders the nullable `environment` as a composed wrapper; the real record sits one level in.
            var environment = created?.Environment?.EnvironmentResponse;
            var mode = environment?.SpiffeAttestationMode;
            if (!string.IsNullOrWhiteSpace(mode))
            {
                var isStrict = string.Equals(mode, "Strict", StringComparison.OrdinalIgnoreCase);
                output.WriteInfo(
                    $"Attestation mode: {mode} — "
                    + (isStrict ? Spiffe.SpiffeSetModeSettings.StrictMeaning : Spiffe.SpiffeSetModeSettings.LaxMeaning));
                if (isStrict)
                {
                    var slug = environment?.Slug;
                    output.WriteInfo(
                        $"Use 'bella spiffe set-mode --lax -p {projectSlug}"
                        + (string.IsNullOrWhiteSpace(slug) ? string.Empty : $" -e {slug}")
                        + "' for a development environment.");
                }
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
            output.WriteError($"Failed to create environment: {ex.Message}");
            return 1;
        }
    }
}
