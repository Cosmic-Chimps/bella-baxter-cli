using System.ComponentModel;
using System.Reflection;
using BellaCli.Commands.Spiffe;
using Spectre.Console.Cli;

namespace BellaBaxter.Cli.Tests.Commands;

/// <summary>
/// Spec 081 T020 (FR-017) — <c>--node-type</c> accepts exactly <c>k8s</c>, <c>aws-iid</c> and <c>nomad</c>. Anything
/// else is refused rather than silently treated as Kubernetes, which is what a typo (<c>nomd</c>) used to become.
/// </summary>
public class NodeTypeIsValidatedTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("k8s")]
    [InlineData("aws-iid")]
    [InlineData("nomad")]
    [InlineData("NOMAD")]
    public void The_three_node_types_and_no_value_are_accepted(string? nodeType)
    {
        Assert.True(new SpiffeAgentSettings { NodeType = nodeType }.Validate().Successful);
        Assert.True(new SpiffeWhoAmISettings { NodeType = nodeType }.Validate().Successful);
    }

    [Theory]
    [InlineData("nomd")]
    [InlineData("aws")]
    [InlineData("kubernetes")]
    [InlineData("gcp-iit")]
    public void Anything_else_is_refused_naming_the_accepted_values(string nodeType)
    {
        foreach (var result in new[]
        {
            new SpiffeAgentSettings { NodeType = nodeType }.Validate(),
            new SpiffeWhoAmISettings { NodeType = nodeType }.Validate(),
        })
        {
            Assert.False(result.Successful);
            Assert.Contains(nodeType, result.Message!, StringComparison.Ordinal);
            Assert.Contains("k8s", result.Message!, StringComparison.Ordinal);
            Assert.Contains("aws-iid", result.Message!, StringComparison.Ordinal);
            Assert.Contains("nomad", result.Message!, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(typeof(SpiffeAgentSettings))]
    [InlineData(typeof(SpiffeWhoAmISettings))]
    public void The_option_description_lists_all_three(Type settings)
    {
        var description = settings.GetProperty("NodeType")!.GetCustomAttribute<DescriptionAttribute>()!.Description;

        Assert.Contains("k8s", description, StringComparison.Ordinal);
        Assert.Contains("aws-iid", description, StringComparison.Ordinal);
        Assert.Contains("nomad", description, StringComparison.Ordinal);
    }
}
