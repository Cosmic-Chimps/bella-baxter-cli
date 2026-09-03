using System.Text.Json;
using BellaCli.Services;
using BellaCli.Services.Spiffe;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// Spec 001 T023 (US2) — what <c>bella spiffe whoami</c> reports about local node evidence.
/// </summary>
/// <remarks>
/// This command exists for one situation: attestation is being refused and nobody knows why. Two
/// causes account for most of it and both are visible locally — no service-account token mounted, and
/// a token that is present but empty. The second is the nasty one: it fails as a SIGNATURE error, so
/// the operator goes and audits the cluster's OIDC trust while the actual problem is a mount.
///
/// <para>So these tests are about the MESSAGES as much as the states. A report that says "no evidence"
/// without naming <c>automountServiceAccountToken</c> sends someone to search documentation; naming it
/// ends the investigation.</para>
/// </remarks>
public class NodeEvidenceTests
{
    private const string TokenPath = "/var/run/secrets/kubernetes.io/serviceaccount/token";
    private const string NamespacePath = "/var/run/secrets/kubernetes.io/serviceaccount/namespace";

    [Fact]
    public void Off_Kubernetes_there_is_no_node_evidence_and_that_is_not_a_problem()
    {
        // A VM or a developer laptop attests on workload selectors alone. Reporting a Problem here
        // would train the reader to ignore the field on the platform where it matters.
        var report = NodeEvidence.Inspect(
            fileExists: _ => false,
            readFile: _ => string.Empty,
            platform: WorkloadPlatform.None);

        Assert.Equal(WorkloadPlatform.None, report.Platform);
        Assert.Null(report.NodeType);
        Assert.Null(report.Problem);
        Assert.False(report.TokenPresent);
    }

    [Fact]
    public void A_missing_token_names_automountServiceAccountToken()
    {
        // The commonest cause, and the fix is one line of YAML — but only if the reader knows which
        // line. "No node evidence found" would not tell them.
        var report = NodeEvidence.Inspect(
            fileExists: _ => false,
            readFile: _ => string.Empty,
            platform: WorkloadPlatform.Kubernetes);

        Assert.Equal("k8s", report.NodeType);
        Assert.False(report.TokenPresent);
        Assert.Contains("automountServiceAccountToken", report.Problem!, StringComparison.Ordinal);
        Assert.Contains(TokenPath, report.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_EMPTY_token_says_the_signature_failure_will_mislead()
    {
        // The diagnosis this command is worth having for. An empty token produces a signature error at
        // the server, which reads as a cluster-trust problem — so the message explicitly says it is
        // not one, rather than leaving the reader to discover that after an afternoon.
        var report = NodeEvidence.Inspect(
            fileExists: path => path == TokenPath,
            readFile: _ => "   ",
            platform: WorkloadPlatform.Kubernetes);

        Assert.False(report.TokenPresent);
        Assert.Contains("empty", report.Problem!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("signature", report.Problem!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unreadable_token_reports_the_read_error_rather_than_claiming_absence()
    {
        // Present-but-unreadable is a permissions problem, not a mounting one. Reporting it as absent
        // would send the operator to fix a mount that is already correct.
        var report = NodeEvidence.Inspect(
            fileExists: path => path == TokenPath,
            readFile: _ => throw new UnauthorizedAccessException("denied"),
            platform: WorkloadPlatform.Kubernetes);

        Assert.False(report.TokenPresent);
        Assert.Contains("could not be read", report.Problem!, StringComparison.Ordinal);
        Assert.Contains("denied", report.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_usable_token_reports_no_problem_and_the_namespace()
    {
        var report = NodeEvidence.Inspect(
            fileExists: path => path is TokenPath or NamespacePath,
            readFile: path => path == NamespacePath ? "payments\n" : "a.real.token",
            platform: WorkloadPlatform.Kubernetes);

        Assert.True(report.TokenPresent);
        Assert.Null(report.Problem);
        Assert.Equal("payments", report.Namespace);
        Assert.Equal(TokenPath, report.TokenPath);
    }

    [Fact]
    public void A_missing_namespace_file_costs_context_not_the_verdict()
    {
        // The namespace is display context. Losing it must not turn a usable token into a problem.
        var report = NodeEvidence.Inspect(
            fileExists: path => path == TokenPath,
            readFile: _ => "a.real.token",
            platform: WorkloadPlatform.Kubernetes);

        Assert.True(report.TokenPresent);
        Assert.Null(report.Problem);
        Assert.Null(report.Namespace);
    }

    [Fact]
    public void The_report_NEVER_contains_the_token_itself()
    {
        // whoami output gets pasted into tickets and chat. A service-account token is a bearer
        // credential for the cluster's identity — the report deliberately carries whether one exists
        // and where, never what it says.
        const string secret = "eyJhbGciOiJSUzI1NiIsImtpZCI6InNlY3JldC12YWx1ZSJ9";

        var report = NodeEvidence.Inspect(
            fileExists: path => path is TokenPath or NamespacePath,
            readFile: path => path == NamespacePath ? "payments" : secret,
            platform: WorkloadPlatform.Kubernetes);

        var json = JsonSerializer.Serialize(report);

        Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
    }

    [Fact]
    public void The_platform_serialises_as_a_NAME_not_an_ordinal()
    {
        // `"platform":0` tells a consumer nothing, and silently changes meaning if a member is ever
        // inserted into the enum. Caught by actually running the command and reading its output.
        var report = NodeEvidence.Inspect(
            fileExists: _ => false,
            readFile: _ => string.Empty,
            platform: WorkloadPlatform.Kubernetes);

        var json = JsonSerializer.Serialize(report);

        Assert.Contains("\"Kubernetes\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"platform\":6", json, StringComparison.Ordinal);
    }
}
