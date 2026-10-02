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
            readFile: path => path == NamespacePath ? "payments\n" : UsableJwt,
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
            readFile: _ => UsableJwt,
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

    // ── spec 064: which token, and what it says about itself ──────────────────

    /// <summary>A JWT-shaped token: real base64url header and payload, an opaque signature segment.</summary>
    private static string FakeJwt(object payload, string signature = "c2lnbmF0dXJlLXNlZ21lbnQtbXVzdC1uZXZlci1hcHBlYXI")
    {
        static string B64(string json) =>
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64("{\"alg\":\"RS256\",\"kid\":\"k\"}")}.{B64(JsonSerializer.Serialize(payload))}.{signature}";
    }

    private static readonly string UsableJwt = FakeJwt(new { iss = "https://oidc.test", aud = new[] { "bella-spiffe" }, exp = 4102444800 });

    private static readonly NodeTokenLocation Conventional = new(NodeTokenPath.Conventional, NodeTokenPathSource.Conventional);

    // Only the projected token exists — no kubelet mount, so no namespace file either (the documented manifest).
    private static bool OnlyConventional(string path) => path == NodeTokenPath.Conventional;

    [Fact]
    public void A_projected_token_with_the_default_mount_disabled_is_detected_and_presented()
    {
        // FR-012. Before spec 064 detection was keyed on the kubelet path, so this pod — the exact one the documented
        // manifest produces — was detected as "no platform" and sent no evidence, with no warning.
        var report = NodeEvidence.Inspect(
            fileExists: p => p == NodeTokenPath.Conventional,
            readFile: _ => UsableJwt,
            getEnvironmentVariable: _ => null);

        Assert.Equal(WorkloadPlatform.Kubernetes, report.Platform);
        Assert.Equal("k8s", report.NodeType);
        Assert.True(report.TokenPresent);
        Assert.Equal(NodeTokenPath.Conventional, report.TokenPath);
        Assert.Equal("conventional", report.TokenPathSource);
        Assert.Null(report.Problem);
    }

    [Fact]
    public void An_explicit_path_that_is_missing_is_a_problem_and_never_falls_back()
    {
        // FR-013: the kubelet token exists here, and must NOT be presented instead.
        var report = NodeEvidence.Inspect(
            location: new NodeTokenLocation("/configured/token", NodeTokenPathSource.Environment),
            fileExists: p => p == NodeTokenPath.KubeletDefault,
            readFile: _ => UsableJwt,
            getEnvironmentVariable: _ => null);

        Assert.False(report.TokenPresent);
        Assert.Equal("/configured/token", report.TokenPath);
        Assert.Contains("/configured/token", report.Problem!, StringComparison.Ordinal);
        Assert.Contains(NodeTokenPath.EnvironmentVariable, report.Problem!, StringComparison.Ordinal);
        Assert.Contains("not fall back", report.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void The_token_says_its_audience_issuer_and_expiry_marked_unverified()
    {
        var token = FakeJwt(new { iss = "https://oidc.test/id/c", aud = new[] { "bella-spiffe", "other" }, exp = 4102444800 });

        var report = NodeEvidence.Inspect(location: Conventional, fileExists: OnlyConventional, readFile: _ => token, getEnvironmentVariable: _ => null);

        Assert.Equal(["bella-spiffe", "other"], report.Audiences!);
        Assert.Equal("https://oidc.test/id/c", report.Issuer);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(4102444800), report.ExpiresAt);
        Assert.True(report.Unverified);
    }

    [Fact]
    public void A_single_string_audience_is_reported_too()
    {
        var token = FakeJwt(new { iss = "https://oidc.test", aud = "https://kubernetes.default.svc", exp = 4102444800 });

        var report = NodeEvidence.Inspect(location: Conventional, fileExists: OnlyConventional, readFile: _ => token, getEnvironmentVariable: _ => null);

        Assert.Equal(["https://kubernetes.default.svc"], report.Audiences!);
    }

    [Fact]
    public void A_file_that_is_not_a_token_is_a_problem()
    {
        var report = NodeEvidence.Inspect(location: Conventional, fileExists: OnlyConventional, readFile: _ => "not-a-jwt", getEnvironmentVariable: _ => null);

        Assert.False(report.TokenPresent);
        Assert.Contains("not a JWT", report.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void Neither_the_token_nor_its_signature_appears_in_the_report()
    {
        // FR-015. whoami output is pasted into tickets; the decoded claims are not secret, the token is a bearer
        // credential, and the signature segment alone is what makes it one.
        const string signature = "U0lHTkFUVVJFLVNIT1VMRC1ORVZFUi1MRUFL";
        var token = FakeJwt(new { iss = "https://oidc.test", aud = "bella-spiffe", exp = 4102444800 }, signature);

        var report = NodeEvidence.Inspect(location: Conventional, fileExists: OnlyConventional, readFile: _ => token, getEnvironmentVariable: _ => null);
        var json = JsonSerializer.Serialize(report);

        Assert.DoesNotContain(token, json, StringComparison.Ordinal);
        Assert.DoesNotContain(signature, json, StringComparison.Ordinal);
        Assert.DoesNotContain(token.Split('.')[1], json, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_inspection_reads_the_file_again_because_projected_tokens_rotate()
    {
        // FR-014 — the agent re-reads per attestation; a value captured once would be stale by the first renewal.
        var current = FakeJwt(new { iss = "https://oidc.test", aud = "first", exp = 4102444800 });
        string Read(string _) => current;

        var first = NodeEvidence.Inspect(location: Conventional, fileExists: OnlyConventional, readFile: Read, getEnvironmentVariable: _ => null);
        current = FakeJwt(new { iss = "https://oidc.test", aud = "second", exp = 4102444800 });
        var second = NodeEvidence.Inspect(location: Conventional, fileExists: OnlyConventional, readFile: Read, getEnvironmentVariable: _ => null);

        Assert.Equal(["first"], first.Audiences!);
        Assert.Equal(["second"], second.Audiences!);
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
