using System.ComponentModel;
using BellaBaxter.Client;
using BellaBaxter.Client.Models;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Microsoft.Kiota.Abstractions;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Pki;

public sealed class AdoptPkiAuthoritySettings : CommandSettings
{
    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("-e|--env|--environment <SLUG>")]
    public string? Environment { get; init; }

    [CommandOption("-f|--force")]
    [Description("Adopt without the confirmation prompt (required when there is no terminal to ask).")]
    public bool Force { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

/// <summary>
/// #1147 — <c>bella pki adopt</c>: declare that this environment's trust material is installed, so its
/// certificates (ACME included) are issued by its OWN certificate authority instead of the tenant-wide one
/// (spec 049, <c>POST …/pki/authority/adoption</c>).
/// </summary>
/// <remarks>
/// <para>The platform cannot see what a host trusts, so adoption is the operator's declaration — which is why
/// the consequence is printed before it is made, and why a run with no terminal must say <c>--force</c>.</para>
/// <para>Exit codes, so a script need not parse the text: 0 adopted (or already was), 1 refused or failed,
/// 2 the environment has no authority of its own yet (configure it first), 3 transient — the secret store could
/// not be asked or ACME could not be moved; nothing was recorded, retry.</para>
/// </remarks>
public sealed class AdoptPkiAuthorityCommand(BellaClientProvider provider, ContextService context, IOutputWriter output)
    : AsyncCommand<AdoptPkiAuthoritySettings>
{
    /// <summary>What adoption does, said before it is done.</summary>
    public const string Consequence =
        "Certificates for this environment, including ACME, will be issued by its own certificate authority. "
        + "Any host or client that has not been given its CA certificate will reject them. The tenant-wide "
        + "authority stays in place, and adoption cannot be undone from the CLI.";

    public const int Adopted = 0;
    public const int Failed = 1;
    public const int NotPrepared = 2;
    public const int Transient = 3;

    /// <summary>How a refusal reads, and which exit code it gets. Keyed on the problem type, then the status.</summary>
    public static (int ExitCode, string Message) Classify(ApiException ex)
    {
        // A client generated after the slice declares typed problems surfaces ProblemDetails; today's surfaces a
        // bare ApiException with the status only. Both are handled, and the status alone is enough to act on.
        var type = (ex as ProblemDetails)?.Type;
        return (type, ex.ResponseStatusCode) switch
        {
            ("authority-not-prepared", _) or (_, 409) => (NotPrepared,
                "This environment has no certificate authority of its own yet. Run 'bella pki configure', install "
                + "the CA certificate it publishes ('bella pki ca') wherever its certificates are verified, then adopt."),
            ("acme-not-enabled", _) => (Transient,
                "ACME could not be moved onto this environment's certificate authority, so nothing was recorded. "
                + "Retry once the secret store is reachable."),
            ("authority-unreadable", _) => (Transient,
                "The secret store could not be asked whether this environment's own certificate authority exists, so "
                + "nothing was recorded. Retry once the secret store is reachable."),
            (_, 503) => (Transient,
                "The server could not confirm the adoption (secret store unreachable, or ACME could not be moved), so "
                + "nothing was recorded. Retry once the secret store is reachable."),
            (_, 404) => (Failed,
                "Environment not found, or you are not allowed to administer its certificate authority."),
            _ => (Failed, $"Adoption failed (HTTP {ex.ResponseStatusCode}); nothing was recorded."),
        };
    }

    protected override async Task<int> ExecuteAsync(CommandContext ctx, AdoptPkiAuthoritySettings settings, CancellationToken ct)
    {
        provider.ApplyOutputModeOverrides(settings.Json);

        BellaClient client;
        try { client = provider.CreateClient(); }
        catch (InvalidOperationException)
        {
            output.WriteError("Not logged in. Run 'bella login' first.");
            return Failed;
        }

        try
        {
            var (projectSlug, _, _) = await context.ResolveProjectAsync(settings.Project, client, ct);
            var (envSlug, _, _) = await context.ResolveEnvironmentAsync(settings.Environment, projectSlug, client, ct);

            if (!settings.Force)
            {
                if (!Interactivity.IsInteractive(output))
                {
                    output.WriteError("Use --force to adopt without confirmation.");
                    return Failed;
                }

                AnsiConsole.MarkupLine(
                    $"About to adopt the own certificate authority of [bold]{Markup.Escape(projectSlug)}/{Markup.Escape(envSlug)}[/].");
                AnsiConsole.MarkupLine($"  {Markup.Escape(Consequence)}");
                if (!AnsiConsole.Confirm("Have you installed its CA certificate everywhere it is verified, and adopt now?", defaultValue: false))
                {
                    output.WriteInfo("Cancelled. Nothing was recorded.");
                    return Adopted;
                }
            }

            AdoptPkiAuthorityResponse? result = null;
            await output.StatusAsync("Adopting the environment's own certificate authority...", async () =>
            {
                result = await client.Api.V1.Projects[projectSlug].Environments[envSlug].Pki.Authority.Adoption
                    .PostAsync(cancellationToken: ct);
            });

            if (settings.Json || output is JsonOutputWriter)
            {
                output.WriteObject(new
                {
                    project = projectSlug,
                    environment = envSlug,
                    status = result?.Status,
                    mountPath = result?.MountPath,
                });
                return Adopted;
            }

            output.WriteSuccess(
                $"{projectSlug}/{envSlug} now issues from its own certificate authority ({result?.MountPath}).");
            if (!string.IsNullOrWhiteSpace(result?.Notice))
                output.WriteInfo(result.Notice);
            return Adopted;
        }
        catch (ApiException ex)
        {
            var (code, message) = Classify(ex);
            output.WriteError(message);
            return code;
        }
        catch (InvalidOperationException ex)
        {
            output.WriteError(ex.Message);
            return Failed;
        }
        catch (Exception ex)
        {
            output.WriteError($"Adoption failed: {ex.Message}");
            return Failed;
        }
    }
}
