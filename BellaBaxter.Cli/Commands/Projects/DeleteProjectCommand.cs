using System.ComponentModel;
using BellaBaxter.Client;
using BellaBaxter.Client.Models;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Microsoft.Kiota.Abstractions;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Projects;

public class DeleteProjectSettings : CommandSettings
{
    [CommandArgument(0, "<identifier>")]
    public string Identifier { get; init; } = "";

    /// <summary>One flag, two spellings: <c>--force</c> is the pre-094 name and keeps working.</summary>
    [CommandOption("-y|--yes|-f|--force")]
    [Description("Skip the prompt, not the summary. Requires --confirm.")]
    public bool Yes { get; init; }

    [CommandOption("--confirm <SLUG>")]
    [Description("The project slug, sent with the preview digest as the confirmation.")]
    public string? Confirm { get; init; }

    [CommandOption("--dry-run")]
    [Description("Print what deleting would destroy, then stop.")]
    public bool DryRun { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

/// <summary>
/// Spec 094 — <c>bella projects delete</c>: shows what the delete destroys, then asks for the project's slug.
/// </summary>
/// <remarks>
/// <para>The positional identifier is never the confirmation: the API compares <c>confirmProjectSlug</c> with the slug
/// of the project it resolves, and a script that passed the identifier twice would be confirming nothing. The
/// confirmation is the prompt answer or <c>--confirm</c>, sent with the digest of the preview this run printed.</para>
/// <para>Exit codes: 0 deleted, dry run or cancelled; 1 missing flag, not found or any other failure; 2 the API
/// refused (422 or 409); 3 the API could not be sure (503), nothing was deleted.</para>
/// </remarks>
public class DeleteProjectCommand(BellaClientProvider provider, IOutputWriter output)
    : AsyncCommand<DeleteProjectSettings>
{
    public const int Deleted = 0;
    public const int Failed = 1;
    public const int Refused = 2;
    public const int Transient = 3;

    private const string ProblemPrefix = "https://bellabaxter.dev/problems/";

    /// <summary>How a refusal reads, and which exit code it gets. Keyed on the problem code, then the status.</summary>
    public static (int ExitCode, string Message) Classify(ApiException ex)
    {
        var code = ProblemCode((ex as ProblemDetails)?.Type);
        return (code, ex.ResponseStatusCode) switch
        {
            ("project-deletion-confirmation-missing", _) => (Refused,
                "The delete needs the project slug as its confirmation. Pass --confirm <slug>. Nothing was deleted."),
            ("project-deletion-confirmation-mismatch", _) => (Refused,
                "The confirmation is not the project's slug, character for character. Nothing was deleted."),
            ("project-deletion-preview-stale", _) => (Refused,
                "The project changed after the summary was shown, so it was not deleted. Run the command again to see what it destroys now."),
            ("project-deletion-preview-unavailable", _) or (_, 503) => (Transient,
                "The server could not work out, or confirm, what deleting would destroy, so nothing was deleted. Retry once the secret store is reachable."),
            (_, 422) => (Refused, "The server refused the confirmation. Nothing was deleted."),
            (_, 409) => (Refused, "The project changed after the summary was shown, so it was not deleted. Run the command again."),
            (_, 404) => (Failed, "Project not found or not allowed."),
            _ => (Failed, $"Project deletion failed (HTTP {ex.ResponseStatusCode}); nothing was deleted."),
        };
    }

    private static string? ProblemCode(string? type)
    {
        if (string.IsNullOrEmpty(type)) return null;
        return type.StartsWith(ProblemPrefix, StringComparison.Ordinal) ? type[ProblemPrefix.Length..] : type;
    }

    /// <summary>The confirmation rule: the exact slug, nothing else. Case and whitespace count.</summary>
    public static bool IsConfirmed(string? answer, string slug) =>
        answer is not null && string.Equals(answer, slug, StringComparison.Ordinal);

    /// <summary>The summary a person reads before anything is deleted, one line each, unescaped.</summary>
    public static IReadOnlyList<string> RenderSummary(ProjectDeletionPreview preview)
    {
        var lines = new List<string>
        {
            $"Deleting project '{preview.Slug}' ({preview.Name}) destroys:",
        };

        var providers = preview.Providers ?? [];
        if (providers.Count == 0)
        {
            lines.Add("  No secrets will be deleted from any provider.");
        }
        else
        {
            foreach (var p in providers)
            {
                var where = p.IsSystemProvider == true ? "" : " in your own account";
                var secrets = (p.EnvironmentSecrets ?? 0) + (p.ProjectSecrets ?? 0);
                var gone = p.ProviderDeleted == true ? " The provider itself is deleted too." : "";
                lines.Add(
                    $"  Provider '{p.Name}' ({p.Type}){where}: {p.EnvironmentSecrets ?? 0} environment secret(s) and "
                    + $"{p.ProjectSecrets ?? 0} project secret(s) are deleted ({secrets} in all).{gone}");
            }
        }

        var t = preview.Totals;
        lines.Add(
            $"  {t?.Environments ?? 0} environment(s), {t?.Webhooks ?? 0} webhook(s), {t?.NotificationChannels ?? 0} notification channel(s), "
            + $"{t?.ActiveLeases ?? 0} active lease(s), {t?.TrustDomains ?? 0} trust domain(s), {t?.SshRoles ?? 0} SSH role(s), "
            + $"{t?.TotpKeys ?? 0} TOTP key(s); {t?.ApiKeysRevoked ?? 0} API key(s) are revoked.");
        lines.Add(preview.DataKeyDeleted == true
            ? "  The project's data key is deleted."
            : "  The project's data key is kept.");
        lines.Add("Secret values deleted this way cannot be restored from Bella.");
        return lines;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, DeleteProjectSettings settings, CancellationToken ct)
    {
        provider.ApplyOutputModeOverrides(settings.Json);

        BellaClient client;
        try { client = provider.CreateClient(); }
        catch (InvalidOperationException)
        {
            output.WriteError("Not logged in. Run 'bella login' first.");
            return Failed;
        }

        return await RunAsync(
            client, settings, Interactivity.IsInteractive(output),
            line => AnsiConsole.MarkupLine(Markup.Escape(line)),
            slug => AnsiConsole.Prompt(new TextPrompt<string>($"Type the project slug [bold]{Markup.Escape(slug)}[/] to delete it:").AllowEmpty()),
            ct);
    }

    /// <summary>The command's flow with its terminal edges injected, so a test drives it without a TTY.</summary>
    public async Task<int> RunAsync(
        BellaClient client, DeleteProjectSettings settings, bool interactive,
        Action<string> printLine, Func<string, string?> askSlug, CancellationToken ct)
    {
        var json = settings.Json || output is JsonOutputWriter;
        try
        {
            ProjectDeletionPreview? preview = null;
            await output.StatusAsync("Working out what deleting would destroy...", async () =>
            {
                preview = await client.Api.V1.Projects[settings.Identifier].DeletionPreview.GetAsync(cancellationToken: ct);
            });
            if (preview is null || string.IsNullOrEmpty(preview.Slug) || string.IsNullOrEmpty(preview.PreviewDigest))
            {
                output.WriteError("The server did not return what deleting would destroy; nothing was deleted.");
                return Failed;
            }

            if (!json)
                foreach (var line in RenderSummary(preview))
                    printLine(line);

            if (settings.DryRun)
            {
                if (json) output.WriteObject(new { preview = Shape(preview) });
                else output.WriteInfo("Dry run: nothing was deleted.");
                return Deleted;
            }

            string confirmation;
            if (settings.Yes)
            {
                if (string.IsNullOrEmpty(settings.Confirm))
                {
                    output.WriteError("--yes skips the prompt, not the confirmation. Add --confirm <slug> with the project's slug.");
                    return Failed;
                }
                confirmation = settings.Confirm;
            }
            else if (!interactive)
            {
                output.WriteError("There is no terminal to ask. Pass --yes --confirm <slug> to delete, or --dry-run to only see the summary.");
                return Failed;
            }
            else
            {
                var answer = askSlug(preview.Slug);
                if (!IsConfirmed(answer, preview.Slug))
                {
                    output.WriteInfo("Cancelled.");
                    return Deleted;
                }
                confirmation = answer!;
            }

            await output.StatusAsync("Deleting project...", async () =>
            {
                await client.Api.V1.Projects[settings.Identifier].DeleteAsync(cfg =>
                {
                    cfg.QueryParameters.ConfirmProjectSlug = confirmation;
                    cfg.QueryParameters.PreviewDigest = preview.PreviewDigest;
                }, ct);
            });

            const string Message = "Project deleted successfully";
            if (json)
            {
                output.WriteObject(new { preview = Shape(preview), result = new { success = true, message = Message } });
                return Deleted;
            }

            output.WriteSuccess($"Project '{preview.Slug}' deleted.");
            output.WriteInfo("The cascade runs in the background; its result appears in Audit Logs.");
            return Deleted;
        }
        catch (ApiException ex)
        {
            var (code, message) = Classify(ex);
            output.WriteError(message);
            return code;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to delete project: {ex.Message}");
            return Failed;
        }
    }

    private static object Shape(ProjectDeletionPreview p) => new
    {
        projectId = p.ProjectId,
        slug = p.Slug,
        name = p.Name,
        providers = (p.Providers ?? []).Select(x => new
        {
            providerId = x.ProviderId,
            name = x.Name,
            type = x.Type,
            isSystemProvider = x.IsSystemProvider,
            providerDeleted = x.ProviderDeleted,
            environmentSecrets = x.EnvironmentSecrets,
            projectSecrets = x.ProjectSecrets,
        }).ToList(),
        totals = new
        {
            environments = p.Totals?.Environments,
            webhooks = p.Totals?.Webhooks,
            notificationChannels = p.Totals?.NotificationChannels,
            activeLeases = p.Totals?.ActiveLeases,
            trustDomains = p.Totals?.TrustDomains,
            sshRoles = p.Totals?.SshRoles,
            totpKeys = p.Totals?.TotpKeys,
            apiKeysRevoked = p.Totals?.ApiKeysRevoked,
        },
        dataKeyDeleted = p.DataKeyDeleted,
        previewDigest = p.PreviewDigest,
    };
}
