using BellaCli.Services.Spiffe;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// Spec 081 T019 (FR-018, FR-020, FR-022) — which Nomad identity the agent presents, and the order it looks in.
/// </summary>
/// <remarks>
/// The order is the operator's explicit word, then the file Nomad writes for the named identity, then the identity's
/// environment variable. The file comes before the variable because Nomad refreshes the file on renewal and does NOT
/// refresh the variable unless <c>change_mode = "restart"</c> (measured on 2.0.7). A variable-first order would
/// present a stale token after the first renewal.
/// </remarks>
public class NomadIdentityPathTests
{
    private const string SecretsDir = "/alloc/task/secrets";
    private const string BellaFile = SecretsDir + "/nomad_bella.jwt";

    /// <summary>An environment that remembers every key asked of it, so "never read" is provable.</summary>
    private sealed class Env(Dictionary<string, string> values)
    {
        public List<string> Asked { get; } = [];

        public string? Get(string key)
        {
            Asked.Add(key);
            return values.GetValueOrDefault(key);
        }
    }

    private static Dictionary<string, string> Vars(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    [Fact]
    public void An_explicit_path_wins_over_everything()
    {
        var env = new Env(Vars(("NOMAD_SECRETS_DIR", SecretsDir), ("NOMAD_TOKEN_bella", "x.y.z"), ("BELLA_NOMAD_IDENTITY_PATH", "/from/env")));

        var location = NomadIdentityPath.Resolve("/from/flag", null, env.Get, _ => true);

        Assert.Equal(NomadIdentitySource.Explicit, location.Source);
        Assert.Equal("/from/flag", location.Path);
        Assert.True(location.IsExplicit);
    }

    [Fact]
    public void The_environment_variable_names_the_path_when_there_is_no_flag()
    {
        var env = new Env(Vars(("NOMAD_SECRETS_DIR", SecretsDir), ("BELLA_NOMAD_IDENTITY_PATH", "/from/env")));

        var location = NomadIdentityPath.Resolve(null, null, env.Get, _ => true);

        Assert.Equal(NomadIdentitySource.Environment, location.Source);
        Assert.Equal("/from/env", location.Path);
        Assert.True(location.IsExplicit);
    }

    [Fact]
    public void An_explicit_path_that_does_not_exist_is_still_returned_so_the_caller_can_refuse()
    {
        // FR-020: never a reason to fall back. The file Nomad wrote exists here and must NOT be chosen instead.
        var env = new Env(Vars(("NOMAD_SECRETS_DIR", SecretsDir)));

        var location = NomadIdentityPath.Resolve("/missing", null, env.Get, p => p == BellaFile);

        Assert.Equal("/missing", location.Path);
        Assert.Equal(NomadIdentitySource.Explicit, location.Source);
    }

    [Fact]
    public void Without_an_explicit_path_the_named_identitys_file_in_the_secrets_directory_is_next()
    {
        var env = new Env(Vars(("NOMAD_SECRETS_DIR", SecretsDir), ("NOMAD_TOKEN_bella", "x.y.z")));

        var location = NomadIdentityPath.Resolve(null, null, env.Get, p => p == BellaFile);

        Assert.Equal(NomadIdentitySource.SecretsFile, location.Source);
        Assert.Equal(BellaFile, location.Path);
        Assert.False(location.IsExplicit);
    }

    [Fact]
    public void The_identitys_environment_variable_is_last()
    {
        var env = new Env(Vars(("NOMAD_SECRETS_DIR", SecretsDir), ("NOMAD_TOKEN_bella", "x.y.z")));

        var location = NomadIdentityPath.Resolve(null, null, env.Get, _ => false);

        Assert.Equal(NomadIdentitySource.TokenVariable, location.Source);
        Assert.Equal("NOMAD_TOKEN_bella", location.Variable);
        Assert.Null(location.Path);
    }

    [Fact]
    public void With_no_source_at_all_nothing_is_found()
    {
        var location = NomadIdentityPath.Resolve(null, null, new Env([]).Get, _ => false);

        Assert.Equal(NomadIdentitySource.NotFound, location.Source);
        Assert.Null(location.Path);
        Assert.Null(location.Variable);
        Assert.False(location.IsExplicit);
    }

    [Fact]
    public void The_identity_name_defaults_to_bella_and_is_configurable_by_flag_and_variable()
    {
        Assert.Equal("bella", NomadIdentityPath.Resolve(null, null, new Env([]).Get, _ => false).Name);

        var byVariable = new Env(Vars(("BELLA_NOMAD_IDENTITY", "svc"), ("NOMAD_SECRETS_DIR", SecretsDir)));
        var viaVariable = NomadIdentityPath.Resolve(null, null, byVariable.Get, p => p == SecretsDir + "/nomad_svc.jwt");
        Assert.Equal("svc", viaVariable.Name);
        Assert.Equal(SecretsDir + "/nomad_svc.jwt", viaVariable.Path);

        var byFlag = new Env(Vars(("BELLA_NOMAD_IDENTITY", "svc"), ("NOMAD_TOKEN_other", "x.y.z")));
        var viaFlag = NomadIdentityPath.Resolve(null, "other", byFlag.Get, _ => false);
        Assert.Equal("other", viaFlag.Name);
        Assert.Equal("NOMAD_TOKEN_other", viaFlag.Variable);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("a b")]
    public void A_name_that_could_leave_the_secrets_directory_is_refused(string name)
    {
        var env = new Env(Vars(("NOMAD_SECRETS_DIR", SecretsDir)));

        var location = NomadIdentityPath.Resolve(null, name, env.Get, _ => true);

        Assert.Equal(NomadIdentitySource.NotFound, location.Source);
        Assert.NotNull(location.Problem);
        Assert.Contains(name, location.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void The_plain_NOMAD_TOKEN_is_never_read()
    {
        // FR-021: NOMAD_TOKEN is an ACL token (a secret UUID) in the common deployment. Not even asked for.
        var env = new Env(Vars(("NOMAD_TOKEN", "11111111-2222-3333-4444-555555555555"), ("NOMAD_SECRETS_DIR", SecretsDir)));

        var location = NomadIdentityPath.Resolve(null, null, env.Get, _ => false);

        Assert.Equal(NomadIdentitySource.NotFound, location.Source);
        Assert.DoesNotContain("NOMAD_TOKEN", env.Asked);
    }

    [Fact]
    public void No_project_file_is_consulted_the_resolver_has_no_way_to_ask_for_one()
    {
        // FR-022: a `.bella` arrives with every cloned repository. The resolver takes no project-setting reader, so a
        // committed file cannot choose which local file is sent to the server it names.
        var parameters = typeof(NomadIdentityPath).GetMethod(nameof(NomadIdentityPath.Resolve))!.GetParameters();

        Assert.DoesNotContain(parameters, p => p.Name!.Contains("project", StringComparison.OrdinalIgnoreCase));
    }
}
