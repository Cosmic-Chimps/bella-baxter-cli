using BellaCli.Infrastructure;
using BellaCli.Services;

namespace BellaBaxter.Cli.Tests.Commands.Environments;

/// <summary>
/// Pilot F8: the documented <c>-e|--environment</c> flag did not exist on the <c>environments</c>
/// subcommands (a Spectre parse error, which reads as a broken command), and <c>--project</c> was
/// silently ignored when the environment came from <c>/keys/me</c> or <c>.bella</c>.
/// </summary>
public class EnvironmentArgumentTests
{
    [Fact]
    public void The_positional_form_still_works()
    {
        var merged = ArgumentMerge.Environment("dev", null);
        Assert.Equal("dev", merged.Value);
        Assert.Null(merged.Error);
    }

    [Fact]
    public void The_flag_form_works()
    {
        var merged = ArgumentMerge.Environment(null, "dev");
        Assert.Equal("dev", merged.Value);
        Assert.Null(merged.Error);
    }

    [Fact]
    public void Neither_form_leaves_the_value_to_context_resolution()
    {
        var merged = ArgumentMerge.Environment(null, null);
        Assert.Null(merged.Value);
        Assert.Null(merged.Error);
    }

    [Theory]
    [InlineData("dev", "dev")]
    [InlineData("dev", "DEV")]
    [InlineData("  dev  ", "dev")]
    public void Both_forms_agreeing_is_not_a_conflict(string positional, string option)
    {
        var merged = ArgumentMerge.Environment(positional, option);
        Assert.Equal("dev", merged.Value);
        Assert.Null(merged.Error);
    }

    [Fact]
    public void Two_different_environments_are_refused_not_reconciled()
    {
        // Picking either one acts on an environment the operator did not ask for, and only they
        // know which is the typo.
        var merged = ArgumentMerge.Environment("dev", "prod");
        Assert.Null(merged.Value);
        Assert.NotNull(merged.Error);
        Assert.Contains("dev", merged.Error);
        Assert.Contains("prod", merged.Error);
    }

    [Fact]
    public void Whitespace_is_not_a_value()
    {
        // A shell that expands an unset variable into "" must not look like an explicit choice.
        Assert.Null(ArgumentMerge.Environment("   ", null).Value);
        Assert.Null(ArgumentMerge.Environment(null, "\t").Value);
        Assert.Null(ArgumentMerge.Environment("", "").Error);
    }

    [Fact]
    public void The_project_merge_follows_the_same_rule()
    {
        Assert.Equal("api", ArgumentMerge.Project("api", null).Value);
        Assert.Equal("api", ArgumentMerge.Project(null, "api").Value);

        var conflict = ArgumentMerge.Project("api", "web");
        Assert.Null(conflict.Value);
        Assert.NotNull(conflict.Error);
        Assert.Contains("--project", conflict.Error);
    }
}

/// <summary>
/// The other half of pilot F8: <c>--project B</c> silently operated on project A's environment,
/// because the environment resolved from the API key's scope or from <c>.bella</c> was never
/// checked against the project being acted on. The command SUCCEEDED, which is the worst shape a
/// context bug can take.
/// </summary>
public class ProjectMembershipTests
{
    [Fact]
    public void An_api_key_scoped_elsewhere_is_refused()
    {
        var error = ProjectMembership.Check(
            EnvironmentSource.ApiKey,
            environmentsProject: "project-a",
            requestedProject: "project-b",
            envSlug: "dev"
        );

        Assert.NotNull(error);
        Assert.Contains("project-a", error);
        Assert.Contains("project-b", error);
    }

    [Fact]
    public void A_bella_file_from_another_project_is_refused_and_says_what_to_do()
    {
        var error = ProjectMembership.Check(
            EnvironmentSource.BellaFile,
            environmentsProject: "project-a",
            requestedProject: "project-b",
            envSlug: "dev"
        );

        Assert.NotNull(error);
        Assert.Contains(".bella", error);
        Assert.Contains("dev", error);
        Assert.Contains("-e", error);
    }

    [Theory]
    [InlineData("project-a", "project-a")]
    [InlineData("Project-A", "project-a")]
    [InlineData(" project-a ", "project-a")]
    public void The_matching_case_passes(string environmentsProject, string requestedProject)
    {
        Assert.Null(
            ProjectMembership.Check(
                EnvironmentSource.ApiKey,
                environmentsProject,
                requestedProject,
                "dev"
            )
        );
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_source_that_names_no_project_is_not_a_mismatch(string? environmentsProject)
    {
        // A Manager/Admin API key has no project scope, and a `.bella` may carry only an
        // environment. Silence is not evidence of a conflict — refusing here would break every
        // unscoped key.
        Assert.Null(
            ProjectMembership.Check(
                EnvironmentSource.ApiKey,
                environmentsProject,
                "project-b",
                "dev"
            )
        );
    }
}
