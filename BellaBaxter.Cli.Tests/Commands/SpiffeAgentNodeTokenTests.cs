using BellaCli.Commands.Spiffe;
using BellaCli.Services;
using BellaCli.Services.Spiffe;

namespace BellaBaxter.Cli.Tests.Commands;

/// <summary>
/// Spec 064 T034 — what the agent decides about its node token before it attests anything.
/// </summary>
public class SpiffeAgentNodeTokenTests
{
    private static NodeEvidenceReport Report(bool present, string? problem = null) =>
        new(WorkloadPlatform.Kubernetes, "k8s", "/p", present, null, problem);

    [Theory]
    [InlineData(NodeTokenPathSource.Explicit)]
    [InlineData(NodeTokenPathSource.Environment)]
    [InlineData(NodeTokenPathSource.ProjectFile)]
    public void An_explicitly_configured_token_that_is_unusable_stops_the_agent(NodeTokenPathSource source)
    {
        // FR-013 — no fallback: starting on another token would pass until enforcement and then fail everywhere.
        var refusal = SpiffeAgentCommand.NodeTokenStartupRefusal(
            new NodeTokenLocation("/configured", source), Report(present: false, problem: "No token at /configured"));

        Assert.Equal("No token at /configured", refusal);
    }

    [Theory]
    [InlineData(NodeTokenPathSource.Conventional)]
    [InlineData(NodeTokenPathSource.KubeletDefault)]
    public void A_discovered_token_with_a_problem_only_warns_as_before(NodeTokenPathSource source)
    {
        // Node evidence is required only in Strict, which the agent cannot know before it asks.
        Assert.Null(SpiffeAgentCommand.NodeTokenStartupRefusal(
            new NodeTokenLocation("/p", source), Report(present: false, problem: "missing")));
    }

    [Fact]
    public void Presenting_the_kubelet_default_token_warns_that_enforcement_will_refuse_it()
    {
        var warning = SpiffeAgentCommand.KubeletDefaultTokenWarning(
            new NodeTokenLocation(NodeTokenPath.KubeletDefault, NodeTokenPathSource.KubeletDefault), Report(present: true));

        Assert.NotNull(warning);
        Assert.Contains("audience", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void A_projected_token_raises_no_kubelet_warning()
    {
        Assert.Null(SpiffeAgentCommand.KubeletDefaultTokenWarning(
            new NodeTokenLocation(NodeTokenPath.Conventional, NodeTokenPathSource.Conventional), Report(present: true)));
    }
}
