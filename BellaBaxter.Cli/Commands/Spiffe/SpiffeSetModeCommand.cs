using BellaBaxter.Client;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Spiffe;

// Spec 001 T031 (US3, FR-019) — `bella spiffe set-mode`.
//
// Attestation policy is per environment, and until now the only way to set it was the WebApp. That
// matters more than convenience: Strict mode plus the AWS account allow-list is what stops a leaked
// bootstrap token from becoming somebody else's identity, and a control reachable only by clicking is
// a control that never makes it into anyone's provisioning script.
//
// THE ONE THING TO BE CAREFUL WITH is the three-valued semantics the API deliberately exposes: an
// omitted field means "leave alone", an empty one means "clear". A CLI that sent every field on every
// invocation would erase whichever settings the operator did not happen to mention — so a flag that
// was not passed is not sent, and clearing is its own explicit flag.

public class SpiffeSetModeSettings : CommandSettings
{
    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("-e|--environment <SLUG>")]
    public string? Environment { get; init; }

    // Spec 028 (FR-019): the descriptions ARE the two mode sentences the API publishes as
    // `modeMeaning` (SpiffeModeMeaning on the server). SpiffeSetModeSettingsTests pins the key phrases so
    // they cannot quietly diverge from what the console shows.
    public const string StrictMeaning =
        "Strict: node evidence is cryptographically verified and at least one constraint must be "
        + "verified against it.";

    public const string LaxMeaning =
        "Lax: constraints are not evaluated. The environment's bootstrap token is the only gate; "
        + "anyone holding it can obtain any workload identity here.";

    [CommandOption("--strict")]
    [System.ComponentModel.Description(StrictMeaning)]
    public bool Strict { get; init; }

    [CommandOption("--lax")]
    [System.ComponentModel.Description(LaxMeaning + " Development only.")]
    public bool Lax { get; init; }

    [CommandOption("--acknowledge-strict-refusals")]
    [System.ComponentModel.Description(
        "Switch to Strict even though the workloads the server lists will then be refused "
        + "(they have no verifiable constraint).")]
    public bool AcknowledgeStrictRefusals { get; init; }

    [CommandOption("--svid-ttl <MINUTES>")]
    [System.ComponentModel.Description("Default SVID lifetime in minutes (1-1440).")]
    public int? SvidTtlMinutes { get; init; }

    [CommandOption("--k8s-oidc <URL>")]
    [System.ComponentModel.Description(
        "Kubernetes OIDC discovery URL used to verify service-account tokens.")]
    public string? K8sOidcUrl { get; init; }

    [CommandOption("--clear-k8s-oidc")]
    [System.ComponentModel.Description("Stop verifying Kubernetes evidence against any cluster issuer.")]
    public bool ClearK8sOidc { get; init; }

    [CommandOption("--aws-account <ID>")]
    [System.ComponentModel.Description(
        "12-digit AWS account id allowed to attest here. Repeatable; replaces the whole list.")]
    public string[] AwsAccounts { get; init; } = [];

    [CommandOption("--clear-aws-accounts")]
    [System.ComponentModel.Description(
        "Remove the AWS account allow-list. Any AWS account's signed instance document may then attest.")]
    public bool ClearAwsAccounts { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }

    public override Spectre.Console.ValidationResult Validate()
    {
        if (Strict && Lax)
        {
            return Spectre.Console.ValidationResult.Error("Pass either --strict or --lax, not both.");
        }

        if (!Strict && !Lax)
        {
            // Required rather than defaulted. Defaulting to --lax would silently loosen an environment
            // somebody had deliberately set to Strict; defaulting to --strict would break attestation
            // for anyone running `set-mode` to change only the TTL.
            return Spectre.Console.ValidationResult.Error("Pass --strict or --lax to say which mode to set.");
        }

        if (SvidTtlMinutes is < 1 or > 1440)
        {
            return Spectre.Console.ValidationResult.Error("--svid-ttl must be between 1 and 1440 minutes.");
        }

        if (ClearK8sOidc && !string.IsNullOrWhiteSpace(K8sOidcUrl))
        {
            return Spectre.Console.ValidationResult.Error(
                "Pass either --k8s-oidc or --clear-k8s-oidc, not both.");
        }

        if (ClearAwsAccounts && AwsAccounts.Length > 0)
        {
            return Spectre.Console.ValidationResult.Error(
                "Pass either --aws-account or --clear-aws-accounts, not both.");
        }

        // Caught here rather than at the server so the operator sees which value is wrong next to the
        // flag they typed. The server refuses it too — this is the friendlier of two closed doors.
        var malformed = AwsAccounts
            .Select(a => a.Trim())
            .Where(a => a.Length > 0 && !(a.Length == 12 && a.All(char.IsAsciiDigit)))
            .ToList();
        if (malformed.Count > 0)
        {
            return Spectre.Console.ValidationResult.Error(
                $"--aws-account must be a 12-digit AWS account id. Not valid: {string.Join(", ", malformed)}.");
        }

        return Spectre.Console.ValidationResult.Success();
    }
}

public class SpiffeSetModeCommand(
    BellaClientProvider provider,
    ContextService context,
    IOutputWriter output)
    : AsyncCommand<SpiffeSetModeSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext ctx, SpiffeSetModeSettings settings, CancellationToken ct)
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
            var (projectSlug, _, _) = await context.ResolveProjectAsync(settings.Project, client, ct);
            var (envSlug, _, _) = await context.ResolveEnvironmentAsync(settings.Environment, projectSlug, client, ct);

            var command = new BellaBaxter.Client.Models.UpdateSpiffeSettingsCommand
            {
                AttestationMode = settings.Strict ? "Strict" : "Lax",
                // Only sent when asked for: the API reads an absent field as "keep what is recorded",
                // and a CLI that always sent a value would overwrite settings nobody mentioned.
                DefaultSvidTtlMinutes = settings.SvidTtlMinutes,
                K8sOidcDiscoveryUrl = settings.ClearK8sOidc
                    ? string.Empty
                    : string.IsNullOrWhiteSpace(settings.K8sOidcUrl) ? null : settings.K8sOidcUrl.Trim(),
                AwsIidAllowedAccounts = settings.ClearAwsAccounts
                    ? []
                    : settings.AwsAccounts.Length > 0
                        ? settings.AwsAccounts.Select(a => a.Trim()).ToList()
                        : null,
                AcknowledgeStrictRefusals = settings.AcknowledgeStrictRefusals,
            };

            BellaBaxter.Client.Models.SpiffeSettingsResponse? result;
            try
            {
                result = await client.Api.V1.Projects[projectSlug].Environments[envSlug]
                    .SpiffeSettings.PutAsync(command, cancellationToken: ct);
            }
            catch (BellaBaxter.Client.Models.ProblemDetails problem) when (problem.ResponseStatusCode == 409)
            {
                // Spec 028 (FR-017): Strict would refuse the named workloads. Nothing was changed. The
                // operator either fixes the registrations or says, explicitly, that they mean it.
                output.WriteError(problem.Detail ?? "Strict mode would refuse workloads in this environment.");
                output.WriteInfo("Re-run with --acknowledge-strict-refusals to switch anyway.");
                return 1;
            }

            if (result is null)
            {
                output.WriteError("The API accepted the request but returned no settings.");
                return 1;
            }

            if (settings.Json || output is JsonOutputWriter)
            {
                // A shape of our own, not the Kiota model: the generated type carries serialiser
                // plumbing that would leak into the JSON contract and change shape on the next regen.
                output.WriteObject(new
                {
                    project = projectSlug,
                    environment = envSlug,
                    attestationMode = result.AttestationMode,
                    defaultSvidTtlMinutes = result.DefaultSvidTtlMinutes,
                    k8sOidcDiscoveryUrl = result.K8sOidcDiscoveryUrl,
                    awsIidAllowedAccounts = result.AwsIidAllowedAccounts ?? [],
                    awsIidAllowedAccountsNotice = result.AwsIidAllowedAccountsNotice,
                    modeMeaning = result.ModeMeaning is null ? null : new { lax = result.ModeMeaning.Lax, strict = result.ModeMeaning.Strict },
                    strictReadiness = new
                    {
                        workloadsWithoutVerifiableConstraint = result.StrictReadiness?.WorkloadsWithoutVerifiableConstraint ?? [],
                    },
                });
                return 0;
            }

            output.WriteSuccess($"SPIFFE settings updated for '{projectSlug}/{envSlug}'.");

            var table = new Table().Border(TableBorder.Rounded)
                .AddColumn("Setting").AddColumn("Value");
            table.AddRow("Attestation mode", result.AttestationMode ?? "—");
            table.AddRow("Default SVID TTL", $"{result.DefaultSvidTtlMinutes} min");
            table.AddRow("k8s OIDC issuer", string.IsNullOrEmpty(result.K8sOidcDiscoveryUrl)
                ? "[yellow]not configured[/]" : result.K8sOidcDiscoveryUrl);
            table.AddRow("AWS accounts",
                result.AwsIidAllowedAccounts is { Count: > 0 } accts
                    ? string.Join(", ", accts)
                    : "[yellow]none[/]");
            AnsiConsole.Write(table);

            // Printed whenever the allow-list is empty, because an empty list is not a safe default:
            // any AWS account's signed instance identity document can attest, and only each workload's
            // own aws:account selector narrows it. A blank row would imply the opposite.
            if (result.AwsIidAllowedAccounts is null or { Count: 0 }
                && !string.IsNullOrWhiteSpace(result.AwsIidAllowedAccountsNotice))
            {
                output.WriteWarning(result.AwsIidAllowedAccountsNotice);
            }

            // Spec 028 (FR-019): say what the resulting mode checks, in the server's own words. Lax is a
            // warning because it verifies nothing; Strict is information.
            var isLax = string.Equals(result.AttestationMode, "Lax", StringComparison.OrdinalIgnoreCase);
            var meaning = isLax ? result.ModeMeaning?.Lax ?? SpiffeSetModeSettings.LaxMeaning : result.ModeMeaning?.Strict ?? SpiffeSetModeSettings.StrictMeaning;
            if (isLax)
                output.WriteWarning(meaning + " Use --strict outside development.");
            else
                output.WriteInfo(meaning);

            var refused = result.StrictReadiness?.WorkloadsWithoutVerifiableConstraint ?? [];
            if (!isLax && refused.Count > 0)
            {
                output.WriteWarning(
                    $"Strict now refuses {refused.Count} workload(s) with no verifiable constraint: "
                    + $"{string.Join(", ", refused)}. Add a verifiable --selector or --node to each.");
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
            output.WriteError($"Failed to update SPIFFE settings: {ex.Message}");
            return 1;
        }
    }
}
