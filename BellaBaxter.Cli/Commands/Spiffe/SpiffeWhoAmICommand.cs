using BellaCli.Infrastructure;
using BellaCli.Services.Spiffe;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Spiffe;

// Spec 001 T023 (US2) — `bella spiffe whoami`.
//
// The LOCAL half of the contract's whoami: what evidence can this host present? It reads the
// filesystem and the environment and reports what would be sent to /attest. Deliberately makes no
// network call, because the situation it exists for is "attestation is being refused and I do not know
// why" — a command that needed the network would be unavailable exactly then.
//
// It is not `bella spiffe status`. Status reports a RUNNING agent's current SVID, which a separate
// process cannot read from another process's memory; that needs the local socket listener and arrives
// with US6. Shipping a status command that could only ever say "no agent" would be worse than not
// shipping one.

public class SpiffeWhoAmISettings : CommandSettings
{
    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class SpiffeWhoAmICommand(IOutputWriter output, GlobalSettings global)
    : Command<SpiffeWhoAmISettings>
{
    protected override int Execute(
        CommandContext context, SpiffeWhoAmISettings settings, CancellationToken ct)
    {
        var report = NodeEvidence.Inspect();

        // `--json` OR an auto-selected JSON mode (API-key auth, or stdout redirected). Checking only
        // the flag meant a piped invocation printed NOTHING at all: the human writer's info lines are
        // suppressed in JSON mode, so `bella spiffe whoami | jq` produced silence and exit 0. A
        // diagnostic command that says nothing when redirected is worse than one that errors.
        if (settings.Json || global.IsJsonMode)
        {
            output.WriteObject(report);
            // A problem is still an exit code in JSON mode: a script checking $? must not have to
            // parse the payload to learn that attestation cannot work here.
            return report.Problem is null ? 0 : 1;
        }

        output.WriteInfo($"Platform: {report.Platform}");

        if (report.NodeType is null)
        {
            output.WriteInfo(
                "Node evidence: none. This host will attest on workload selectors alone, which is "
                + "expected on a VM or a developer machine.");
            return 0;
        }

        output.WriteInfo($"Node attestor: {report.NodeType}");
        output.WriteInfo($"Evidence path: {report.TokenPath}");

        if (report.Namespace is not null)
        {
            output.WriteInfo($"Namespace: {report.Namespace}");
        }

        if (report.Problem is not null)
        {
            // The problem text already says what to do — NodeEvidence writes it that way — so it is
            // printed verbatim rather than wrapped in a second layer of explanation.
            output.WriteError(report.Problem);
            return 1;
        }

        output.WriteSuccess("Node evidence is present and readable.");
        return 0;
    }
}
