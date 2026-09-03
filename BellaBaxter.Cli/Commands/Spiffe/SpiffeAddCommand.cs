using BellaBaxter.Client;
using BellaCli.Infrastructure;
using BellaCli.Services;
using BellaCli.Services.Spiffe;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Spiffe;

// Spec 001 T019 (US1, FR-027) — `bella spiffe add`.
//
// NAMING: contracts/cli.md says `add`, the task text says `register`. The contract wins — it is the
// CLI's specified surface, and `add` matches the noun-verb style the rest of this CLI uses.
//
// IT PRINTS WHAT THE SERVER ASSIGNED, not what the client thinks the SPIFFE ID will be. The id is
// derived server-side as `spiffe://{tenant}/{project}/{env}/{name}`, and reproducing that formula here
// would be a second implementation of the same rule, free to drift from the first — the exact defect
// class this feature keeps turning up. So the command creates, then reads the workload back and prints
// the recorded id.
//
// Spec 027 fixed the untyped response this note used to describe: `createWorkloadIdentity` now declares
// [ProducesResponseType<WorkloadIdentityResponse>(201)] and the generated client returns that type, so
// the create's own response is usable. The read-back stays anyway — printing what the server recorded is
// the more truthful thing to do, which is why spec 001 chose it in the first place. Do not "restore" a
// workaround here: there is no longer a defect to work around.

public class SpiffeAddSettings : CommandSettings
{
    [CommandOption("-n|--name <NAME>")]
    [System.ComponentModel.Description("Workload name, unique within the environment.")]
    public string? Name { get; init; }

    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("-e|--environment <SLUG>")]
    public string? Environment { get; init; }

    [CommandOption("--node <TYPE=VALUE>")]
    [System.ComponentModel.Description(
        "Node (infrastructure) attestor, e.g. k8s:cluster=prod or aws:account=123456789012. Repeatable. "
        + "Only types the server can verify are accepted: run 'bella spiffe selector-types'.")]
    public string[] NodeAttestors { get; init; } = [];

    [CommandOption("--selector <TYPE=VALUE>")]
    [System.ComponentModel.Description(
        "Workload selector, e.g. k8s:namespace=payments or k8s:sa=billing-sa. Repeatable. Types the server "
        + "can verify: run 'bella spiffe selector-types'. Any other type is self-asserted.")]
    public string[] Selectors { get; init; } = [];

    [CommandOption("--ttl <MINUTES>")]
    [System.ComponentModel.Description("Lease TTL for tokens issued to this workload (1-1440).")]
    public int? TtlMinutes { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }

    public override Spectre.Console.ValidationResult Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            return Spectre.Console.ValidationResult.Error("--name is required.");
        }

        if (TtlMinutes is < 1 or > 1440)
        {
            return Spectre.Console.ValidationResult.Error("--ttl must be between 1 and 1440 minutes.");
        }

        // Every selector error at once: a registration carries several, and fixing one typo per
        // invocation is a poor way to spend an afternoon.
        var errors = new List<string>();
        errors.AddRange(AttestationSelectorParser.ParseAll(NodeAttestors, "--node").Errors);
        errors.AddRange(AttestationSelectorParser.ParseAll(Selectors, "--selector").Errors);

        return errors.Count > 0
            ? Spectre.Console.ValidationResult.Error(string.Join(" ", errors))
            : Spectre.Console.ValidationResult.Success();
    }
}

public class SpiffeAddCommand(
    BellaClientProvider provider,
    ContextService context,
    IOutputWriter output) : AsyncCommand<SpiffeAddSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext ctx, SpiffeAddSettings settings, CancellationToken ct)
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
        var (nodes, _) = AttestationSelectorParser.ParseAll(settings.NodeAttestors, "--node");
        var (selectors, _) = AttestationSelectorParser.ParseAll(settings.Selectors, "--selector");

        try
        {
            var (projectSlug, _, _) = await context.ResolveProjectAsync(settings.Project, client, ct);
            var (envSlug, _, _) = await context.ResolveEnvironmentAsync(settings.Environment, projectSlug, client, ct);

            var workloads = client.Api.V1.Projects[projectSlug].Environments[envSlug].WorkloadIdentities;

            BellaBaxter.Client.Models.WorkloadIdentityResponse? posted;
            try
            {
                posted = await workloads.PostAsync(
                    new BellaBaxter.Client.Models.CreateWorkloadIdentityCommand
                    {
                        Name = name,
                        IssuedTokenTtlMinutes = settings.TtlMinutes,
                        NodeAttestors = [.. nodes.Select(Dto)],
                        Selectors = [.. selectors.Select(Dto)],
                    },
                    cancellationToken: ct);
            }
            catch (BellaBaxter.Client.Models.ProblemDetails problem) when (problem.ResponseStatusCode == 400)
            {
                // Spec 028 (FR-014): the server refused a constraint that could never work — a node type no
                // attestor serves, or constraints needing two kinds of evidence. Its detail names the type
                // and the served types; print it as-is rather than paraphrasing a security refusal.
                output.WriteError(problem.Detail ?? problem.Title ?? "The registration was refused.");
                return 1;
            }

            // Read back so the printed SPIFFE ID is the one the SERVER recorded. Deriving it here would
            // duplicate the server's rule and be free to drift from it.
            var list = await workloads.GetAsync(cancellationToken: ct);
            var created = (list?.Items ?? [])
                .FirstOrDefault(w => string.Equals(w.Name?.Trim(), name, StringComparison.Ordinal));

            if (created is null)
            {
                // The POST succeeded but the workload is not in the list. Reported rather than assumed
                // away: an operator needs to know the registration is in an unexpected state, not be
                // told it worked.
                output.WriteError(
                    $"'{name}' was accepted but does not appear in {projectSlug}/{envSlug}. "
                    + "Run 'bella spiffe list' to check.");
                return 1;
            }

            if (settings.Json || output is JsonOutputWriter)
            {
                output.WriteObject(new
                {
                    project = projectSlug,
                    environment = envSlug,
                    name = created.Name,
                    spiffeId = created.SpiffeId,
                    workloadIdentityId = created.Id,
                    issuedTokenTtlMinutes = created.IssuedTokenTtlMinutes,
                    nodeAttestors = nodes.Select(n => n.ToString()),
                    selectors = selectors.Select(s => s.ToString()),
                    constraintReport = ReportJson(created.ConstraintReport?.ConstraintReportDto ?? posted?.ConstraintReport?.ConstraintReportDto),
                });
                return 0;
            }

            output.WriteSuccess($"Registered '{name}' in {projectSlug}/{envSlug}.");
            AnsiConsole.MarkupLine($"  SPIFFE ID: [bold]{created.SpiffeId}[/]");

            // Spec 028 (FR-015/FR-016): the server is the single author of "what this registration
            // protects". It warns when every constraint is self-asserted and when there are none at all
            // (the zero-constraint warning that used to be written here). Printed in the same voice as
            // before; exit stays 0 because the registration succeeded.
            var report = created.ConstraintReport?.ConstraintReportDto ?? posted?.ConstraintReport?.ConstraintReportDto;
            foreach (var warning in report?.Warnings ?? [])
                output.WriteWarning(warning);

            return 0;
        }
        catch (InvalidOperationException ex)
        {
            output.WriteError(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to register the workload identity: {ex.Message}");
            return 1;
        }
    }

    private static BellaBaxter.Client.Models.AttestationSelectorDto Dto(AttestationSelectorArgument s) =>
        new() { Type = s.Type, Value = s.Value };

    /// <summary>A shape of our own, not the Kiota model, so the JSON contract does not carry serialiser plumbing.</summary>
    internal static object? ReportJson(BellaBaxter.Client.Models.ConstraintReportDto? report) =>
        report is null ? null : new
        {
            hasVerifiableConstraint = report.HasVerifiableConstraint,
            requiredEvidenceKind = report.RequiredEvidenceKind,
            selectors = (report.Selectors ?? []).Select(v => new { type = v.Type, value = v.Value, verification = v.Verification }),
            nodeAttestors = (report.NodeAttestors ?? []).Select(v => new { type = v.Type, value = v.Value, verification = v.Verification }),
            warnings = report.Warnings ?? [],
        };
}
