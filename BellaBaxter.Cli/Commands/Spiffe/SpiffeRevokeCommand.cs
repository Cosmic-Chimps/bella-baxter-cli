using BellaBaxter.Client;
using BellaCli.Infrastructure;
using BellaCli.Services;
using BellaCli.Services.Spiffe;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Spiffe;

// Spec 001 T036 (US4, FR-024/025) — `bella spiffe revoke --name <n>`.
//
// Revocation is the one SPIFFE operation with an immediate blast radius: the API's delete cascades
// through `RevokeWorkloadLeases`, so every outstanding `bax-` lease the workload holds stops working
// before the call returns. That is the point — it is the emergency handle for a compromised workload —
// and it is why this command does three things a thinner wrapper would not:
//
//   1. It counts the live leases BEFORE revoking, because the delete returns 204 and cannot say what
//      it killed. An operator running this during an incident needs to know whether they just cut off
//      one process or forty. The count is a report taken at that moment, not a guarantee.
//   2. It refuses an ambiguous name instead of picking a row (see `WorkloadResolver`). Guessing here
//      means revoking a workload nobody asked about.
//   3. It confirms by default, and refuses rather than assuming yes when there is no terminal — the
//      same rule the other destructive commands follow, and it matters more here because the effect is
//      immediate and not undoable: a revoked identity is re-registered, not restored.
//
// It does NOT call `revokeWorkloadLeases` afterwards. The delete already invokes that cascade
// synchronously (`bus.InvokeAsync`), so a second call would add a redundant mutation and a second
// audit entry for one operator action.

public class SpiffeRevokeSettings : CommandSettings
{
    [CommandOption("-n|--name <NAME>")]
    [System.ComponentModel.Description("Name of the workload identity to revoke.")]
    public string? Name { get; init; }

    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("-e|--environment <SLUG>")]
    public string? Environment { get; init; }

    [CommandOption("-f|--force")]
    [System.ComponentModel.Description("Revoke without the confirmation prompt.")]
    public bool Force { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }

    public override Spectre.Console.ValidationResult Validate() =>
        string.IsNullOrWhiteSpace(Name)
            ? Spectre.Console.ValidationResult.Error("--name is required: which workload identity to revoke.")
            : Spectre.Console.ValidationResult.Success();
}

public class SpiffeRevokeCommand(
    BellaClientProvider provider,
    ContextService context,
    IOutputWriter output)
    : AsyncCommand<SpiffeRevokeSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext ctx, SpiffeRevokeSettings settings, CancellationToken ct)
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

        var name = settings.Name!.Trim();

        try
        {
            var (projectSlug, _, _) = await context.ResolveProjectAsync(settings.Project, client, ct);
            var (envSlug, _, _) = await context.ResolveEnvironmentAsync(settings.Environment, projectSlug, client, ct);

            var env = client.Api.V1.Projects[projectSlug].Environments[envSlug];

            var list = await env.WorkloadIdentities.GetAsync(cancellationToken: ct);
            var resolution = WorkloadResolver.Resolve(
                name,
                (list?.Items ?? []).Select(w => new WorkloadCandidate(
                    w.Id, w.Name, w.SpiffeId,
                    IsRevoked: w.RevokedAt is not null
                        || string.Equals(w.Status, "Revoked", StringComparison.OrdinalIgnoreCase))));

            if (resolution.Kind == WorkloadResolutionKind.AlreadyRevoked)
            {
                // Not an error: the desired state already holds. Exit 0 so a re-run of a playbook does
                // not fail on work it already did.
                output.WriteInfo(resolution.Problem!);
                return 0;
            }

            if (resolution.Kind != WorkloadResolutionKind.Resolved)
            {
                output.WriteError(resolution.Problem!);
                return 1;
            }

            // Counted before the delete, which returns 204 and so cannot report what it terminated.
            var leases = await env.WorkloadIdentities[resolution.Id].Leases.GetAsync(cancellationToken: ct);
            var activeLeases = leases?.ActiveCount ?? 0;

            if (!settings.Force)
            {
                if (Console.IsOutputRedirected || output is JsonOutputWriter)
                {
                    output.WriteError("Use --force to revoke without confirmation.");
                    return 1;
                }

                AnsiConsole.MarkupLine($"About to revoke [bold]{name}[/] in [bold]{projectSlug}/{envSlug}[/].");
                if (resolution.SpiffeId is not null)
                {
                    AnsiConsole.MarkupLine($"  SPIFFE ID: [dim]{resolution.SpiffeId}[/]");
                }

                // The lease count is the part worth reading twice: it is the number of running things
                // that lose their credential the moment this returns.
                AnsiConsole.MarkupLine(activeLeases == 0
                    ? "  No live leases — nothing is currently using this identity."
                    : $"  [yellow]{activeLeases} live lease(s) will stop working immediately.[/]");

                if (!AnsiConsole.Confirm("Revoke?", defaultValue: false))
                {
                    output.WriteInfo("Cancelled.");
                    return 0;
                }
            }

            await env.WorkloadIdentities[resolution.Id].DeleteAsync(cancellationToken: ct);

            if (settings.Json || output is JsonOutputWriter)
            {
                output.WriteObject(new
                {
                    project = projectSlug,
                    environment = envSlug,
                    name,
                    workloadIdentityId = resolution.Id,
                    spiffeId = resolution.SpiffeId,
                    revoked = true,
                    // Named for what it is — measured just before the revocation, not counted by it.
                    leasesActiveBeforeRevocation = activeLeases,
                });
                return 0;
            }

            output.WriteSuccess($"Workload identity '{name}' revoked.");
            output.WriteInfo(activeLeases == 0
                ? "It held no live leases."
                : $"{activeLeases} lease(s) were active and have been terminated.");
            output.WriteInfo(
                "A revoked identity cannot be restored — register it again if the workload should "
                + "return, and it will receive a new SPIFFE ID.");
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            output.WriteError(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to revoke workload identity: {ex.Message}");
            return 1;
        }
    }
}
