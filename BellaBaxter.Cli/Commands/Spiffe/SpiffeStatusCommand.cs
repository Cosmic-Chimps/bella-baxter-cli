using BellaCli.Infrastructure;
using BellaCli.Services.Spiffe;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Spiffe;

// Spec 001 T023 (US2) — `bella spiffe status`.
//
// It ASKS the running agent over the agent's own Workload API rather than reading any shared state.
// There is no pid file and no status file, deliberately: a file says what was true when it was
// written, and the question here — "is my workload's identity healthy right now" — is exactly the one
// a stale file answers wrongly. Using the same interface a workload uses also means status cannot
// disagree with what a workload would see.
//
// EXIT CODES ARE THE POINT for anything running this from a probe or a script:
//   0  an identity is being served
//   1  no agent, or an agent with no identity
// A command that exited 0 for "the agent is up but has never attested" would make a readiness probe
// pass for a workload that cannot authenticate to anything.

public class SpiffeStatusSettings : CommandSettings
{
    [CommandOption("--socket <PATH>")]
    [System.ComponentModel.Description(
        "Agent endpoint to query. Defaults to SPIFFE_ENDPOINT_SOCKET, else the per-user runtime path.")]
    public string? Socket { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class SpiffeStatusCommand(IOutputWriter output) : AsyncCommand<SpiffeStatusSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, SpiffeStatusSettings settings, CancellationToken ct)
    {
        SvidSocketLocation location;
        try
        {
            location = SvidSocketPath.Resolve(settings.Socket);
        }
        catch (Exception ex)
        {
            output.WriteError(ex.Message);
            return 1;
        }

        var status = await new AgentStatusProbe().ProbeAsync(location, ct);
        var now = DateTimeOffset.UtcNow;
        var remaining = status.TimeRemaining(now);

        if (settings.Json || output is JsonOutputWriter)
        {
            output.WriteObject(new
            {
                state = status.Kind.ToString(),
                socket = status.SocketPath,
                socketSource = status.SocketSource.ToString(),
                spiffeId = status.SpiffeId,
                trustDomain = status.TrustDomain,
                expiresAt = status.ExpiresAt,
                // Negative when the served certificate has already expired — reported rather than
                // clamped, because "-3 minutes" is the fact and zero would read as "just expired".
                secondsRemaining = remaining is null ? (double?)null : Math.Round(remaining.Value.TotalSeconds),
                advice = status.Advice,
                detail = status.Detail,
            });

            return status.Kind == AgentStatusKind.Serving ? 0 : 1;
        }

        var table = new Table().Border(TableBorder.Rounded).AddColumn("").AddColumn("");
        table.AddRow("State", Describe(status.Kind));
        table.AddRow("Socket", $"{status.SocketPath} [dim](from {status.SocketSource})[/]");

        if (status.SpiffeId is not null)
        {
            table.AddRow("SPIFFE ID", status.SpiffeId);
            table.AddRow("Trust domain", status.TrustDomain ?? "[yellow]unparseable[/]");
        }

        if (status.ExpiresAt is not null && remaining is not null)
        {
            table.AddRow("Expires", $"{status.ExpiresAt:u} [dim]({FormatRemaining(remaining.Value)})[/]");
        }

        AnsiConsole.Write(table);

        if (status.Detail is not null)
        {
            output.WriteInfo(status.Detail);
        }

        if (status.Advice is not null)
        {
            output.WriteWarning(status.Advice);
            return 1;
        }

        // A served-but-already-expired certificate is a failure even though an agent is answering: the
        // workload holds something no peer will accept. Reported here rather than as a healthy row.
        if (remaining is { Ticks: <= 0 })
        {
            output.WriteError(
                "The served certificate has already expired. Renewal is failing — check the agent's "
                + "output for the attestation refusal.");
            return 1;
        }

        output.WriteSuccess("The agent is serving a current identity.");
        return 0;
    }

    private static string Describe(AgentStatusKind kind) => kind switch
    {
        AgentStatusKind.Serving => "[green]serving an identity[/]",
        AgentStatusKind.NoIdentity => "[yellow]running, no identity yet[/]",
        AgentStatusKind.NotServing => "[yellow]stale socket, nothing listening[/]",
        _ => "[red]no agent[/]",
    };

    private static string FormatRemaining(TimeSpan remaining) =>
        remaining.Ticks <= 0
            ? $"EXPIRED {FormatSpan(remaining.Negate())} ago"
            : $"{FormatSpan(remaining)} left";

    private static string FormatSpan(TimeSpan span) =>
        span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : span.TotalMinutes >= 1
                ? $"{(int)span.TotalMinutes}m {span.Seconds}s"
                : $"{(int)span.TotalSeconds}s";
}
