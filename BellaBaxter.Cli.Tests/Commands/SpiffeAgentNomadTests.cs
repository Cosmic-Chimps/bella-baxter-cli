using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using BellaCli.Commands.Spiffe;
using BellaCli.Services.Spiffe;
using Spectre.Console.Cli;

namespace BellaBaxter.Cli.Tests.Commands;

/// <summary>
/// Spec 081 T020 (FR-017, FR-019, FR-020, FR-021; US3 scenarios 1 to 4) — what the agent decides about its Nomad
/// identity before and between attestations.
/// </summary>
public sealed class SpiffeAgentNomadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bella-nomad-" + Guid.NewGuid().ToString("N"));

    public SpiffeAgentNomadTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static string B64(string json) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Jwt(long iat) =>
        $"{B64("{\"alg\":\"RS256\"}")}.{B64(JsonSerializer.Serialize(new { iss = "https://n", aud = "a", iat, exp = iat + 3600, nomad_allocation_id = "a1" }))}.c2ln";

    [Fact]
    public void The_agent_presents_the_identity_in_the_file_and_picks_up_a_renewal_without_a_restart()
    {
        var path = Path.Combine(_dir, "nomad_bella.jwt");
        var location = new NomadIdentityLocation(NomadIdentitySource.Explicit, "bella", path, null, null);
        var read = SpiffeAgentCommand.NomadNodeTokenReader(location);

        File.WriteAllText(path, Jwt(1_800_000_000) + "\n");
        var first = read();
        File.WriteAllText(path, Jwt(1_800_001_800) + "\n");
        var second = read();

        Assert.Equal(Jwt(1_800_000_000), first); // trimmed, and exactly the file's content
        Assert.Equal(Jwt(1_800_001_800), second);
    }

    [Fact]
    public void An_acl_token_in_a_file_is_never_presented()
    {
        var path = Path.Combine(_dir, "nomad_bella.jwt");
        File.WriteAllText(path, "11111111-2222-3333-4444-555555555555");

        var read = SpiffeAgentCommand.NomadNodeTokenReader(new NomadIdentityLocation(NomadIdentitySource.Explicit, "bella", path, null, null));

        Assert.Null(read());
    }

    [Theory]
    [InlineData(NomadIdentitySource.Explicit)]
    [InlineData(NomadIdentitySource.Environment)]
    public void An_explicitly_configured_identity_that_is_unusable_stops_the_agent_naming_the_path(NomadIdentitySource source)
    {
        var location = new NomadIdentityLocation(source, "bella", "/configured/identity", null, null);
        var report = NomadEvidence.Inspect(location, _ => false, _ => "", _ => null);

        var refusal = SpiffeAgentCommand.NomadStartupRefusal(location, report);

        Assert.NotNull(refusal);
        Assert.Contains("/configured/identity", refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(NomadIdentitySource.SecretsFile)]
    [InlineData(NomadIdentitySource.TokenVariable)]
    [InlineData(NomadIdentitySource.NotFound)]
    public void A_discovered_identity_with_a_problem_only_warns_because_evidence_is_required_only_in_strict(NomadIdentitySource source)
    {
        var location = new NomadIdentityLocation(source, "bella", "/p", "NOMAD_TOKEN_bella", null);
        var report = NomadEvidence.Inspect(location, _ => false, _ => "", _ => null);

        Assert.NotNull(report.Problem);
        Assert.Null(SpiffeAgentCommand.NomadStartupRefusal(location, report));
    }

    [Fact]
    public void A_usable_explicit_identity_does_not_stop_the_agent()
    {
        var path = Path.Combine(_dir, "id.jwt");
        File.WriteAllText(path, Jwt(1_800_000_000));
        var location = new NomadIdentityLocation(NomadIdentitySource.Explicit, "bella", path, null, null);

        Assert.Null(SpiffeAgentCommand.NomadStartupRefusal(location, NomadEvidence.Inspect(location)));
    }

    [Fact]
    public void The_startup_line_names_the_source_and_never_the_token()
    {
        var path = Path.Combine(_dir, "id.jwt");
        var token = Jwt(1_800_000_000);
        File.WriteAllText(path, token);
        var location = new NomadIdentityLocation(NomadIdentitySource.Explicit, "bella", path, null, null);

        var line = SpiffeAgentCommand.NomadStartupLine(location);

        Assert.Contains(path, line, StringComparison.Ordinal);
        Assert.Contains("explicit", line, StringComparison.Ordinal);
        Assert.DoesNotContain(token, line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_agent_has_the_two_nomad_options_and_says_which_environment_variables_feed_them()
    {
        var options = typeof(SpiffeAgentSettings)
            .GetProperties()
            .Select(p => (Attr: p.GetCustomAttribute<CommandOptionAttribute>(), Description: p.GetCustomAttribute<DescriptionAttribute>()?.Description))
            .Where(o => o.Attr is not null)
            .ToDictionary(o => o.Attr!.LongNames.Single(), o => o.Description);

        Assert.Contains("BELLA_NOMAD_IDENTITY", options["nomad-identity"], StringComparison.Ordinal);
        Assert.Contains("BELLA_NOMAD_IDENTITY_PATH", options["nomad-identity-path"], StringComparison.Ordinal);
    }
}
