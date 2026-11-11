using System.Text.Json;
using BellaCli.Services.Spiffe;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// Spec 081 T020 (FR-019, FR-020, FR-021, FR-023) — what the agent reads as Nomad node evidence, and what <c>whoami</c>
/// may say about it.
/// </summary>
public class NomadEvidenceTests
{
    private const string FilePath = "/alloc/task/secrets/nomad_bella.jwt";
    private const string Signature = "U0lHTkFUVVJFLVNIT1VMRC1ORVZFUi1MRUFL";

    private static string B64(string json) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Jwt(object payload, string signature = Signature) =>
        $"{B64("{\"alg\":\"RS256\",\"kid\":\"k\"}")}.{B64(JsonSerializer.Serialize(payload))}.{signature}";

    // The measured shape of a Nomad 2.0.7 task identity (research R1): flat claims, `aud` a string, exp - iat = TTL.
    private static string TaskIdentity(string alloc = "a1", long iat = 1_800_000_000, long ttl = 3600) => Jwt(new Dictionary<string, object>
    {
        ["iss"] = "https://nomad.example.com",
        ["aud"] = "bella-spiffe/env",
        ["iat"] = iat,
        ["exp"] = iat + ttl,
        ["nomad_namespace"] = "default",
        ["nomad_job_id"] = "billing",
        ["nomad_task"] = "web",
        ["nomad_allocation_id"] = alloc,
    });

    private static NomadIdentityLocation Explicit(string path = FilePath) =>
        new(NomadIdentitySource.Explicit, "bella", path, null, null);

    private static NomadEvidenceReport Inspect(NomadIdentityLocation location, string? content, Func<string, string?>? env = null) =>
        NomadEvidence.Inspect(location, fileExists: p => content is not null && p == location.Path, readFile: _ => content ?? "", getEnvironmentVariable: env ?? (_ => null));

    [Fact]
    public void A_real_looking_task_identity_is_presented_and_its_claims_are_reported_unverified()
    {
        var report = Inspect(Explicit(), TaskIdentity());

        Assert.True(report.TokenPresent);
        Assert.Null(report.Problem);
        Assert.Equal("nomad", report.NodeType);
        Assert.Equal("https://nomad.example.com", report.Issuer);
        Assert.Equal(["bella-spiffe/env"], report.Audiences!);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_800_003_600), report.ExpiresAt);
        Assert.Equal(3600, report.LifetimeSeconds);
        Assert.Equal("billing", report.NomadClaims!["nomad_job_id"]);
        Assert.Equal("web", report.NomadClaims["nomad_task"]);
        Assert.Equal("a1", report.NomadClaims["nomad_allocation_id"]);
        Assert.True(report.Unverified);
    }

    [Fact]
    public void A_service_identity_carries_nomad_service_and_no_task()
    {
        var token = Jwt(new { iss = "https://n", aud = "a", exp = 4102444800, nomad_namespace = "default", nomad_job_id = "j", nomad_service = "api" });

        var report = Inspect(Explicit(), token);

        Assert.Equal("api", report.NomadClaims!["nomad_service"]);
        Assert.False(report.NomadClaims.ContainsKey("nomad_task"));
    }

    [Fact]
    public void The_report_and_the_presentable_token_never_contain_the_token_its_payload_or_its_signature()
    {
        var token = TaskIdentity();
        var json = JsonSerializer.Serialize(Inspect(Explicit(), token));

        Assert.DoesNotContain(token, json, StringComparison.Ordinal);
        Assert.DoesNotContain(Signature, json, StringComparison.Ordinal);
        Assert.DoesNotContain(token.Split('.')[1], json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("11111111-2222-3333-4444-555555555555")] // a Nomad ACL token: a UUID secret
    [InlineData("not-a-jwt")]
    [InlineData("a.b")]
    [InlineData("a.b.c.d")]
    [InlineData("eyJhbGciOiJSUzI1NiJ9.!!!not-base64!!!.sig")]
    public void Only_a_three_segment_jwt_with_a_decodable_payload_is_ever_presented(string content)
    {
        // FR-021. The ACL token must never be sent, whichever source it arrives through.
        var location = Explicit();

        Assert.Null(NomadEvidence.ReadPresentable(location, p => p == location.Path, _ => content, _ => null));
        var report = Inspect(location, content);
        Assert.False(report.TokenPresent);
        Assert.Contains("not a JWT", report.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_acl_token_in_the_identitys_variable_is_not_sent_either()
    {
        var location = new NomadIdentityLocation(NomadIdentitySource.TokenVariable, "bella", null, "NOMAD_TOKEN_bella", null);

        var token = NomadEvidence.ReadPresentable(location, _ => false, _ => "", k => k == "NOMAD_TOKEN_bella" ? "11111111-2222-3333-4444-555555555555" : null);

        Assert.Null(token);
    }

    [Fact]
    public void The_token_is_read_again_on_every_call_so_a_renewal_is_presented_next_time()
    {
        // FR-019: Nomad rewrites the file at about half the TTL; a value captured once is stale by then.
        var location = Explicit();
        var current = TaskIdentity(iat: 1_800_000_000);
        string Read(string _) => current;

        var first = NomadEvidence.ReadPresentable(location, p => p == location.Path, Read, _ => null);
        current = TaskIdentity(iat: 1_800_001_800);
        var second = NomadEvidence.ReadPresentable(location, p => p == location.Path, Read, _ => null);

        Assert.NotNull(first);
        Assert.NotEqual(first, second);
        Assert.Equal(current, second);
    }

    [Fact]
    public void An_explicit_path_that_is_missing_is_a_problem_naming_the_path_and_never_falls_back()
    {
        var report = NomadEvidence.Inspect(
            Explicit("/configured/identity"),
            fileExists: p => p == FilePath, // the file Nomad wrote exists, and must not be used instead
            readFile: _ => TaskIdentity(),
            getEnvironmentVariable: _ => null);

        Assert.False(report.TokenPresent);
        Assert.Contains("/configured/identity", report.Problem!, StringComparison.Ordinal);
        Assert.Contains("not fall back", report.Problem!, StringComparison.Ordinal);
        Assert.Null(NomadEvidence.ReadPresentable(Explicit("/configured/identity"), p => p == FilePath, _ => TaskIdentity(), _ => null));
    }

    [Fact]
    public void An_unreadable_file_reports_the_read_error_not_absence()
    {
        var report = NomadEvidence.Inspect(
            Explicit(), p => true, _ => throw new UnauthorizedAccessException("denied"), _ => null);

        Assert.False(report.TokenPresent);
        Assert.Contains("could not be read", report.Problem!, StringComparison.Ordinal);
        Assert.Contains("denied", report.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_found_says_where_nomad_puts_the_identity()
    {
        var report = NomadEvidence.Inspect(
            new NomadIdentityLocation(NomadIdentitySource.NotFound, "bella", null, null, null),
            _ => false, _ => "", _ => null);

        Assert.False(report.TokenPresent);
        Assert.Contains("file = true", report.Problem!, StringComparison.Ordinal);
        Assert.Contains("'bella'", report.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void The_variable_source_is_read_from_the_environment_and_named_without_its_value()
    {
        var token = TaskIdentity();
        var location = new NomadIdentityLocation(NomadIdentitySource.TokenVariable, "bella", null, "NOMAD_TOKEN_bella", null);

        var report = NomadEvidence.Inspect(location, _ => false, _ => "", k => k == "NOMAD_TOKEN_bella" ? token : null);

        Assert.True(report.TokenPresent);
        Assert.Equal("token-variable", report.Source);
        Assert.Equal("NOMAD_TOKEN_bella", report.Variable);
        Assert.DoesNotContain(token, JsonSerializer.Serialize(report), StringComparison.Ordinal);
    }

    [Fact]
    public void The_extracted_decoder_is_shared_with_node_evidence_and_keeps_its_behaviour()
    {
        // T023: one local decode. A token NodeEvidence accepts as a JWT is accepted by the shared helper, and a non-JWT is not.
        Assert.True(UnverifiedJwtClaims.TryRead(TaskIdentity(), out var claims));
        Assert.Equal("https://nomad.example.com", claims["iss"].GetString());
        Assert.False(UnverifiedJwtClaims.TryRead("not-a-jwt", out _));
        Assert.False(UnverifiedJwtClaims.TryRead("a.e30.c", out _)); // an empty claim set is not evidence
    }
}
