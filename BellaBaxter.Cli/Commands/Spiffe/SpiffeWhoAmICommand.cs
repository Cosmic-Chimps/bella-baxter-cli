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
    [CommandOption("--node-token-path <PATH>")]
    [System.ComponentModel.Description(
        "Kubernetes token to inspect, resolved as the agent resolves it (flag, BELLA_NODE_TOKEN_PATH, .bella, "
        + "/var/run/secrets/bella/token, kubelet default).")]
    public string? NodeTokenPath { get; init; }

    [CommandOption("--node-type <TYPE>")]
    [System.ComponentModel.Description(
        "k8s (default), aws-iid or nomad. aws-iid reads this EC2 instance's identity document over IMDSv2; "
        + "nomad inspects the task's named workload identity.")]
    public string? NodeType { get; init; }

    [CommandOption("--nomad-identity <NAME>")]
    [System.ComponentModel.Description("Nomad: the name of the task's identity block. Also BELLA_NOMAD_IDENTITY; defaults to bella.")]
    public string? NomadIdentity { get; init; }

    [CommandOption("--nomad-identity-path <PATH>")]
    [System.ComponentModel.Description(
        "Nomad: the identity token file to inspect, resolved as the agent resolves it (flag, BELLA_NOMAD_IDENTITY_PATH, "
        + "the named identity's file in $NOMAD_SECRETS_DIR, its NOMAD_TOKEN_<name> variable).")]
    public string? NomadIdentityPath { get; init; }

    [CommandOption("--state-dir <DIR>")]
    [System.ComponentModel.Description("The agent's state directory (aws-iid), as the agent resolves it.")]
    public string? StateDir { get; init; }

    [CommandOption("-e|--environment-id <GUID>")]
    [System.ComponentModel.Description(
        "With --name: report whether the agent holds this instance's re-attestation credential (aws-iid). "
        + "Also BELLA_ENVIRONMENT_ID.")]
    public string? EnvironmentId { get; init; }

    [CommandOption("-n|--name <NAME>")]
    [System.ComponentModel.Description("The workload name (aws-iid credential check). Also BELLA_WORKLOAD_NAME.")]
    public string? WorkloadName { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }

    public override Spectre.Console.ValidationResult Validate() =>
        SpiffeNodeTypes.Refusal(NodeType) is { } refusal
            ? Spectre.Console.ValidationResult.Error(refusal)
            : Spectre.Console.ValidationResult.Success();
}

public class SpiffeWhoAmICommand(IOutputWriter output, GlobalSettings global, IHttpClientFactory httpClientFactory)
    : AsyncCommand<SpiffeWhoAmISettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, SpiffeWhoAmISettings settings, CancellationToken ct) =>
        SpiffeNodeTypes.Normalize(settings.NodeType) switch
        {
            SpiffeNodeTypes.AwsIid => await AwsAsync(settings, ct),
            SpiffeNodeTypes.Nomad => await NomadAsync(settings, ct),
            _ => Kubernetes(settings),
        };

    /// <summary>
    /// Spec 081 (FR-023) — what this Nomad task would present: where the identity came from and its claims, read locally
    /// and NOT verified. The token itself, its payload and its signature are never printed. No network call.
    /// </summary>
    internal Task<int> NomadAsync(SpiffeWhoAmISettings settings, CancellationToken ct)
    {
        var location = NomadIdentityPath.Resolve(settings.NomadIdentityPath, settings.NomadIdentity);
        var report = NomadEvidence.Inspect(location);

        if (settings.Json || global.IsJsonMode)
        {
            output.WriteObject(report);
            // A problem is still an exit code in JSON mode, as for Kubernetes.
            return Task.FromResult(report.Problem is null ? 0 : 1);
        }

        output.WriteInfo($"Node attestor: {report.NodeType}");
        output.WriteInfo($"Identity: {report.IdentityName} (source: {report.Source}: {report.Path ?? report.Variable ?? "nothing found"})");

        if (report.TokenPresent)
        {
            output.WriteInfo("Claims below are decoded locally and NOT verified; Bella verifies at attestation.");
            if (report.Issuer is not null)
                output.WriteInfo($"Issuer: {report.Issuer} (unverified)");
            output.WriteInfo($"Audience: {(report.Audiences is { Count: > 0 } ? string.Join(", ", report.Audiences) : "(none)")} (unverified)");
            if (report.ExpiresAt is { } expires)
            {
                var left = expires - DateTimeOffset.UtcNow;
                output.WriteInfo($"Expires: {expires:yyyy-MM-ddTHH:mm:ssZ} ({(left > TimeSpan.Zero ? $"in {(int)left.TotalMinutes}m" : "EXPIRED")}) (unverified)");
            }
            if (report.LifetimeSeconds is { } lifetime)
                output.WriteInfo($"Lifetime: {lifetime} s (unverified)");
            foreach (var claim in report.NomadClaims!.OrderBy(c => c.Key, StringComparer.Ordinal))
                output.WriteInfo($"{claim.Key}: {claim.Value} (unverified)");
        }

        if (report.Problem is not null)
        {
            output.WriteError(report.Problem);
            return Task.FromResult(1);
        }

        output.WriteSuccess("Nomad identity is present and readable.");
        return Task.FromResult(0);
    }

    /// <summary>
    /// Spec 065 — what this EC2 instance would present: its identity document's claims (read locally and NOT verified)
    /// and whether the agent holds the re-attestation credential. Neither the signature nor the credential is printed.
    /// </summary>
    internal async Task<int> AwsAsync(SpiffeWhoAmISettings settings, CancellationToken ct)
    {
        AwsInstanceFactsView facts;
        try
        {
            facts = await new AwsInstanceEvidence(httpClientFactory.CreateClient(AwsInstanceEvidence.HttpClientName))
                .ReadFactsAsync(ct);
        }
        catch (AwsInstanceEvidenceException ex)
        {
            if (settings.Json || global.IsJsonMode)
                output.WriteObject(new { nodeType = SpiffeAgentCommand.AwsNodeType, problem = ex.Message });
            else
                output.WriteError(ex.Message);
            return 1;
        }

        var envRaw = settings.EnvironmentId ?? System.Environment.GetEnvironmentVariable("BELLA_ENVIRONMENT_ID");
        var name = settings.WorkloadName ?? System.Environment.GetEnvironmentVariable("BELLA_WORKLOAD_NAME");
        string credential = "unknown (pass --environment-id and --name)";
        if (Guid.TryParse(envRaw, out var envId) && !string.IsNullOrWhiteSpace(name))
        {
            var store = new NodeReattestationCredentialStore(AgentStateDirectory.Resolve(settings.StateDir), envId, name);
            credential = store.Current is null ? "absent" : "held";
        }

        if (settings.Json || global.IsJsonMode)
        {
            output.WriteObject(new
            {
                nodeType = SpiffeAgentCommand.AwsNodeType,
                account = facts.Account,
                region = facts.Region,
                instanceId = facts.InstanceId,
                pendingTime = facts.PendingTime,
                credential,
                unverified = true,
            });
            return 0;
        }

        output.WriteInfo($"Node attestor: {SpiffeAgentCommand.AwsNodeType}");
        output.WriteInfo($"Instance: {facts.InstanceId} (unverified)");
        output.WriteInfo($"Account / Region: {facts.Account} / {facts.Region} (unverified)");
        output.WriteInfo($"Launched (pendingTime): {facts.PendingTime} (unverified)");
        output.WriteInfo($"Re-attestation credential: {credential}");
        output.WriteSuccess("The instance identity document is readable over IMDSv2.");
        return 0;
    }

    private int Kubernetes(SpiffeWhoAmISettings settings)
    {
        var report = NodeEvidence.Inspect(BellaCli.Services.Spiffe.NodeTokenPath.Resolve(settings.NodeTokenPath));

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
        output.WriteInfo($"Node token: {report.TokenPath} (source: {report.TokenPathSource})");

        // Spec 064 (FR-015) — what the token says about itself, decoded here and NOT verified; the token itself is
        // never printed. The audience is the line that matters before enforcing: the kubelet default names the
        // cluster's own API server, which an enforcing environment refuses.
        if (report.Audiences is not null && report.TokenPresent)
        {
            output.WriteInfo($"Audience: {(report.Audiences.Count == 0 ? "(none)" : string.Join(", ", report.Audiences))} (unverified)");
            if (report.Issuer is not null)
                output.WriteInfo($"Issuer: {report.Issuer} (unverified)");
            if (report.ExpiresAt is { } expires)
            {
                var left = expires - DateTimeOffset.UtcNow;
                output.WriteInfo($"Expires: {expires:yyyy-MM-ddTHH:mm:ssZ} ({(left > TimeSpan.Zero ? $"in {(int)left.TotalMinutes}m" : "EXPIRED")})");
            }
        }

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
