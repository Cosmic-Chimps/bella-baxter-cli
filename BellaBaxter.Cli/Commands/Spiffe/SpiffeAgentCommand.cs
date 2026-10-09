using BellaBaxter.Client;
using BellaCli.Infrastructure;
using BellaCli.Services;
using BellaCli.Services.Spiffe;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Spiffe;

// Spec 001 T021 (US2) — `bella spiffe agent`.
//
// NOT `bella agent`, which is the shipped secrets-sync sidecar and keeps its name. The two are layers
// rather than peers: the secrets agent needs an identity, this provides one, and T044's SDK
// auto-detection is exactly that composition.
//
// A THIN SHELL. Everything worth testing is in SvidAgent, SvidRenewalPolicy and SvidAgentLoop, which
// have no dependency on Spectre, a console, or a process. What is left here is configuration
// resolution and printing — and configuration resolution is where a sidecar actually fails, so it
// reports what it resolved and refuses when something required is missing rather than starting and
// dying on the first attestation.

public class SpiffeAgentSettings : CommandSettings
{
    [CommandOption("-e|--environment-id <GUID>")]
    [System.ComponentModel.Description(
        "Environment the workload is registered in. Also read from BELLA_ENVIRONMENT_ID.")]
    public string? EnvironmentId { get; init; }

    [CommandOption("-n|--name <NAME>")]
    [System.ComponentModel.Description(
        "Registered workload name. Also read from BELLA_WORKLOAD_NAME.")]
    public string? WorkloadName { get; init; }

    [CommandOption("--node-type <TYPE>")]
    [System.ComponentModel.Description(
        "Node attestor: k8s (default) or aws-iid. aws-iid reads this EC2 instance's identity document over IMDSv2.")]
    public string? NodeType { get; init; }

    [CommandOption("--node-token-path <PATH>")]
    [System.ComponentModel.Description(
        "Kubernetes token to present as node evidence. Also BELLA_NODE_TOKEN_PATH or node_token_path in .bella; "
        + "defaults to /var/run/secrets/bella/token when mounted, else the kubelet's default token.")]
    public string? NodeTokenPath { get; init; }

    [CommandOption("--state-dir <DIR>")]
    [System.ComponentModel.Description(
        "Where the agent keeps its AWS re-attestation credential (owner-only). Also BELLA_AGENT_STATE_DIR; "
        + "defaults to $XDG_STATE_HOME/bella/spiffe-agent, else ~/.local/state/bella/spiffe-agent.")]
    public string? StateDir { get; init; }

    [CommandOption("--socket <PATH>")]
    [System.ComponentModel.Description(
        "Local endpoint path. Defaults to SPIFFE_ENDPOINT_SOCKET, else a per-user runtime path.")]
    public string? Socket { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class SpiffeAgentCommand(
    IHttpClientFactory httpClientFactory,
    ConfigService config,
    IOutputWriter output) : AsyncCommand<SpiffeAgentSettings>
{
    /// <summary>
    /// The bootstrap token is read from the environment ONLY, never a flag.
    /// </summary>
    /// <remarks>
    /// A credential on the command line lands in the shell history, in <c>ps</c> output visible to every
    /// user on the host, and in the container spec that anyone with read access to the namespace can
    /// see. An environment variable is not perfect either, but it is the mechanism Kubernetes secrets
    /// and every CI system already feed, and it does not appear in a process listing.
    /// </remarks>
    public const string BootstrapTokenVariable = "BELLA_BOOTSTRAP_TOKEN";

    /// <summary>Spec 065 — the node type that attests with this EC2 instance's identity document.</summary>
    public const string AwsNodeType = "aws-iid";

    /// <summary>
    /// Spec 064 (FR-013) — why the agent refuses to start, if the node token was configured explicitly and is
    /// unusable. Starting anyway and falling back to another token would pass every check until an environment
    /// enforces token audience, and then fail everywhere at once.
    /// </summary>
    public static string? NodeTokenStartupRefusal(NodeTokenLocation location, NodeEvidenceReport evidence) =>
        location.IsExplicit && evidence.Problem is not null ? evidence.Problem : null;

    /// <summary>
    /// Spec 064 — the one warning a pod on the kubelet's default token gets: an environment enforcing token
    /// audience refuses it, because its audience is the cluster's own API server.
    /// </summary>
    public static string? KubeletDefaultTokenWarning(NodeTokenLocation location, NodeEvidenceReport evidence) =>
        evidence.Platform == WorkloadPlatform.Kubernetes && evidence.TokenPresent
            && location.Source == NodeTokenPathSource.KubeletDefault
            ? "Presenting the kubelet's default service-account token. An environment enforcing token audience "
                + "will refuse it; mount a projected token requested for Bella (see `bella spiffe whoami`)."
            : null;

    protected override async Task<int> ExecuteAsync(
        CommandContext context, SpiffeAgentSettings settings, CancellationToken ct)
    {
        var environmentIdRaw = settings.EnvironmentId
            ?? Environment.GetEnvironmentVariable("BELLA_ENVIRONMENT_ID");
        var workloadName = settings.WorkloadName
            ?? Environment.GetEnvironmentVariable("BELLA_WORKLOAD_NAME");
        var bootstrapToken = Environment.GetEnvironmentVariable(BootstrapTokenVariable);

        // Every missing input is reported at once. A sidecar that fails on one, gets fixed, then fails
        // on the next costs a deploy cycle per mistake.
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(environmentIdRaw)) missing.Add("--environment-id (or BELLA_ENVIRONMENT_ID)");
        if (string.IsNullOrWhiteSpace(workloadName)) missing.Add("--name (or BELLA_WORKLOAD_NAME)");
        if (string.IsNullOrWhiteSpace(bootstrapToken)) missing.Add(BootstrapTokenVariable);

        if (missing.Count > 0)
        {
            output.WriteError($"The agent needs: {string.Join(", ", missing)}.");
            return 1;
        }

        if (!Guid.TryParse(environmentIdRaw, out var environmentId))
        {
            output.WriteError($"'{environmentIdRaw}' is not a valid environment id.");
            return 1;
        }

        // Backlog §2.31 — refused BEFORE anything is prepared or sent. The bootstrap token and the SVID
        // private key travel over this address, so plain http is accepted only to this machine. The
        // message names where the address came from, because the fix is to change THAT source.
        if (config.ApiUrlProblem is { } refusal)
        {
            output.WriteError(refusal);
            return 1;
        }

        var location = SvidSocketPath.Resolve(settings.Socket);

        // Prepared and checked BEFORE the first attestation, so a permission problem is reported while
        // there is nothing sensitive in memory yet — rather than after the agent holds a private key it
        // then cannot serve.
        try
        {
            SvidSocketPath.PrepareDirectory(location.Path);
        }
        catch (Exception ex)
        {
            output.WriteError(ex.Message);
            return 1;
        }

        // Spec 064 — resolved ONCE, from the flag/environment/.bella, so the startup line, the check below and every
        // later attestation agree about which file is the token.
        var nodeToken = NodeTokenPath.Resolve(settings.NodeTokenPath);
        var aws = string.Equals(settings.NodeType, AwsNodeType, StringComparison.OrdinalIgnoreCase);
        var evidence = aws
            ? new NodeEvidenceReport(WorkloadPlatform.None, AwsNodeType, null, false, null, null)
            : NodeEvidence.Inspect(nodeToken);
        if (!aws && NodeTokenStartupRefusal(nodeToken, evidence) is { } nodeTokenRefusal)
        {
            output.WriteError(nodeTokenRefusal);
            return 1;
        }
        var nodeType = settings.NodeType ?? evidence.NodeType ?? "k8s";

        var httpClient = httpClientFactory.CreateClient(nameof(SpiffeAgentCommand));
        // Advisory clients-1 — the node credential is not sent to a server a .bella chose.
        httpClient.BaseAddress = new Uri(config.ApiUrlForSuppliedCredential());

        // Spec 065 — AWS evidence: read once now, so an instance that cannot supply it refuses to start (nothing is
        // sent), and then re-read on EVERY attestation, so a restart's new document is what the next one presents.
        // The credential store is shared by both sources (research R8).
        AwsInstanceEvidence? imds = null;
        NodeReattestationCredentialStore? credentials = null;
        if (aws)
        {
            imds = new AwsInstanceEvidence(httpClientFactory.CreateClient(AwsInstanceEvidence.HttpClientName));
            AwsInstanceFactsView facts;
            try
            {
                facts = await imds.ReadFactsAsync(ct);
            }
            catch (AwsInstanceEvidenceException ex)
            {
                output.WriteError(ex.Message);
                return 1;
            }

            try
            {
                credentials = new NodeReattestationCredentialStore(
                    AgentStateDirectory.Resolve(settings.StateDir), environmentId, workloadName!);
                PrivateFiles.EnsurePrivateDirectory(credentials.StateDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                output.WriteError($"The agent's state directory cannot be created as owner-only: {ex.Message}");
                return 1;
            }

            output.WriteInfo(
                $"Node evidence: aws-iid instance {facts.InstanceId} ({facts.Region}, account {facts.Account}); "
                + $"re-attestation credential: {(credentials.Current is null ? "not yet issued" : "held")}.");
        }

        var attestation = new SvidAttestationRequest(
            environmentId,
            workloadName!,
            bootstrapToken!,
            nodeType,
            // Re-read on every attestation: a Kubernetes projected token is rotated underneath us,
            // so a value captured here would be stale by the first renewal.
            ReadNodeToken: () => !aws && NodeEvidence.Inspect(nodeToken).TokenPresent
                ? File.ReadAllText(nodeToken.Path).Trim()
                : null,
            ReadNodeTokenAsync: imds is null ? null : async token => await imds.ReadEnvelopeAsync(token),
            Credentials: credentials);

        var source = new HttpSvidSource(httpClient, attestation);

        // One request record feeds both sources, so the X.509 and JWT paths can never disagree about
        // which workload in which environment is attesting.
        var jwtSource = new HttpJwtSvidSource(httpClient, attestation);

        // Disposed on the way out (backlog §2.31): clears the held SVID's private key once both tasks
        // below have stopped, so the last identity does not outlive the agent's decision to stop.
        using var agent = new SvidAgent(source);
        var reporter = new ConsoleSvidReporter(output);
        var loop = new SvidAgentLoop(
            agent,
            reporter,
            () => DateTimeOffset.UtcNow,
            (delay, ct) => Task.Delay(delay, ct));

        output.WriteInfo($"Attesting workload '{workloadName}' in environment {environmentId:D}.");
        output.WriteInfo($"Local endpoint: {location.Path} (from {location.Source}).");
        if (evidence.Platform == WorkloadPlatform.Kubernetes)
            output.WriteInfo($"Node token: {nodeToken.Path} (source: {nodeToken.SourceName}).");
        if (KubeletDefaultTokenWarning(nodeToken, evidence) is { } kubeletWarning)
            output.WriteWarning(kubeletWarning);
        if (evidence.Problem is not null)
        {
            // A warning rather than a refusal: node evidence is only required when the environment's
            // policy is Strict, and the agent cannot know the policy before it asks.
            output.WriteWarning(evidence.Problem);
        }

        // The command harness already cancels this token on Ctrl-C / SIGTERM, so the agent uses it
        // rather than installing its own PosixSignalRegistration — two handlers for one signal is a
        // race over who gets to decide the exit code. The loop treats cancellation as a clean stop, so
        // an ordinary pod termination exits 0 instead of printing a stack trace.
        //
        // TWO tasks, one fate (spec 001 T042). The renewal loop keeps the identity fresh; the Workload
        // API server hands it to local workloads over the socket. Neither is useful without the other,
        // so whichever stops first cancels the other:
        //
        //   * the loop dying while the server ran would leave the socket serving an SVID that is never
        //     renewed again — it works, then silently stops working at expiry;
        //   * the server dying while the loop ran would leave a healthy-looking agent that no workload
        //     can reach, and nothing in the logs would say the endpoint is gone.
        //
        // Started BEFORE the first attestation on purpose: a client connecting to a fresh agent blocks
        // on the stream until an identity exists (WorkloadApiService), which is the correct SPIFFE
        // behaviour and much kinder than a connection refused for the first few seconds of a pod's life.
        var server = new WorkloadApiServer(agent, jwtSource, location);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var serverTask = server.RunAsync(linked.Token);
        var loopTask = loop.RunAsync(linked.Token);

        var finished = await Task.WhenAny(serverTask, loopTask);
        await linked.CancelAsync();

        // Both are awaited before returning, so a socket file is not left behind by a process that has
        // already exited — the next start would then have to reason about whether it belongs to a live
        // agent.
        try
        {
            await Task.WhenAll(serverTask, loopTask);
        }
        catch (OperationCanceledException)
        {
            // Expected: the still-running half was cancelled by the one that finished.
        }
        catch (SvidAttestationException ex)
        {
            // The FIRST attestation failed, so there is no identity to serve and nothing to keep
            // running for. The message already says what to do.
            output.WriteError(ex.Message);
            return 1;
        }
        catch (Exception ex) when (finished == serverTask)
        {
            // A bind failure is the common one: a stale socket owned by something else, or a directory
            // whose permissions the agent refuses to widen. Both are operator-fixable and neither is
            // worth a stack trace.
            output.WriteError($"The Workload API endpoint could not be served: {ex.Message}");
            return 1;
        }

        output.WriteInfo("Agent stopped.");
        return 0;
    }

    /// <summary>
    /// Why the agent will not attest to <paramref name="apiUrl"/>, or null when it may. The rule is
    /// <see cref="BellaApiAddress"/>'s — https, or plain http to loopback only, with no opt-out.
    /// </summary>
    internal static string? ApiAddressRefusal(string apiUrl, string source) =>
        ConfigService.ProblemFor(apiUrl, source);

    /// <summary>Prints what the loop reports. The whole of the agent's operator-facing output.</summary>
    private sealed class ConsoleSvidReporter(IOutputWriter output) : ISvidAgentReporter
    {
        public void Rotated(AttestedSvid svid) =>
            // The SPIFFE ID and the expiry, never the key or the certificate body. An operator needs to
            // know WHICH identity and FOR HOW LONG; the material itself in a log is the thing this
            // whole feature exists to stop.
            output.WriteSuccess(
                $"SVID issued for {svid.SpiffeId}, valid until {svid.ExpiresAt:u} "
                + $"({(svid.ExpiresAt - svid.IssuedAt).TotalMinutes:0} min).");

        public void AttestationFailed(string reason, bool identityStillValid) =>
            // The severity depends on whether the workload still HAS an identity. Inside the renewal
            // window a failure is noise the window exists to absorb; after expiry it is an outage. One
            // level for both would either cry wolf or hide the real thing.
            _ = identityStillValid
                ? Warn(reason)
                : Fail(reason);

        public void Waiting(TimeSpan duration) { }

        private bool Warn(string reason)
        {
            output.WriteWarning(
                $"Attestation failed but the current SVID is still valid — will retry. {reason}");
            return true;
        }

        private bool Fail(string reason)
        {
            output.WriteError($"Attestation failed and this workload has NO valid identity. {reason}");
            return false;
        }
    }
}
