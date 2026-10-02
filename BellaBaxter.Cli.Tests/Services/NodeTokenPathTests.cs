using BellaCli.Services.Spiffe;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// Spec 064 FR-011 / FR-013 — which Kubernetes token the agent presents as node evidence.
/// </summary>
/// <remarks>
/// <para>Before spec 064 the answer was always the kubelet's default token, whose audience is the cluster's own API
/// server — exactly the replayable one. An environment enforcing token audience refuses it, so the agent must be able
/// to present a PROJECTED token requested for Bella, and the rollout depends on that working before enforcement is
/// switched on.</para>
/// <para>The precedence is the operator's explicit word first (flag, then environment, then the project file), then
/// the documented conventional path when it exists — so the documented manifest needs no flag at all — and only then
/// the kubelet default.</para>
/// </remarks>
public class NodeTokenPathTests
{
    private static Func<string, string?> Env(string? value) =>
        name => name == NodeTokenPath.EnvironmentVariable ? value : null;

    private static Func<string, string?> Bella(string? value) =>
        key => key == NodeTokenPath.ProjectFileKey ? value : null;

    private static Func<string, bool> Exists(params string[] paths) => p => paths.Contains(p);

    [Fact]
    public void The_flag_wins_over_everything()
    {
        var location = NodeTokenPath.Resolve(
            explicitPath: "/flag/token",
            getEnvironmentVariable: Env("/env/token"),
            readProjectSetting: Bella("/bella/token"),
            fileExists: Exists(NodeTokenPath.Conventional));

        Assert.Equal("/flag/token", location.Path);
        Assert.Equal(NodeTokenPathSource.Explicit, location.Source);
        Assert.True(location.IsExplicit);
    }

    [Fact]
    public void The_environment_variable_comes_next()
    {
        var location = NodeTokenPath.Resolve(null, Env("/env/token"), Bella("/bella/token"), Exists(NodeTokenPath.Conventional));

        Assert.Equal("/env/token", location.Path);
        Assert.Equal(NodeTokenPathSource.Environment, location.Source);
        Assert.True(location.IsExplicit);
    }

    [Fact]
    public void Then_the_project_file()
    {
        var location = NodeTokenPath.Resolve(null, Env(null), Bella("/bella/token"), Exists(NodeTokenPath.Conventional));

        Assert.Equal("/bella/token", location.Path);
        Assert.Equal(NodeTokenPathSource.ProjectFile, location.Source);
        Assert.True(location.IsExplicit);
    }

    [Fact]
    public void Then_the_conventional_projected_path_but_only_when_it_exists()
    {
        var present = NodeTokenPath.Resolve(null, Env(null), Bella(null), Exists(NodeTokenPath.Conventional, NodeTokenPath.KubeletDefault));
        Assert.Equal(NodeTokenPath.Conventional, present.Path);
        Assert.Equal(NodeTokenPathSource.Conventional, present.Source);
        Assert.False(present.IsExplicit);

        var absent = NodeTokenPath.Resolve(null, Env(null), Bella(null), Exists(NodeTokenPath.KubeletDefault));
        Assert.Equal(NodeTokenPath.KubeletDefault, absent.Path);
        Assert.Equal(NodeTokenPathSource.KubeletDefault, absent.Source);
        Assert.False(absent.IsExplicit);
    }

    [Fact]
    public void An_explicit_path_is_returned_even_when_it_does_not_exist_so_the_caller_can_refuse()
    {
        // FR-013: the resolver must not quietly step past a configured path to a file that happens to exist. Falling
        // back to the kubelet token would pass every check before enforcement and fail the moment it is turned on.
        var location = NodeTokenPath.Resolve(null, Env("/configured/but/missing"), Bella(null), Exists(NodeTokenPath.KubeletDefault));

        Assert.Equal("/configured/but/missing", location.Path);
        Assert.True(location.IsExplicit);
    }

    [Fact]
    public void Blank_values_are_absent_rather_than_a_path()
    {
        var location = NodeTokenPath.Resolve("  ", Env(" "), Bella(""), Exists());

        Assert.Equal(NodeTokenPathSource.KubeletDefault, location.Source);
    }

    [Theory]
    [InlineData(NodeTokenPathSource.Explicit, "explicit")]
    [InlineData(NodeTokenPathSource.Environment, "environment")]
    [InlineData(NodeTokenPathSource.ProjectFile, "project-file")]
    [InlineData(NodeTokenPathSource.Conventional, "conventional")]
    [InlineData(NodeTokenPathSource.KubeletDefault, "kubelet-default")]
    public void The_source_has_a_stable_display_name(NodeTokenPathSource source, string name) =>
        Assert.Equal(name, new NodeTokenLocation("/p", source).SourceName);
}
