using BellaCli.Commands.Run;
using BellaCli.Services;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// Spec 064 FR-020 — the audience the keyless flow asks a CI provider for, and the `bella run` defect found beside it.
/// </summary>
/// <remarks>
/// A trust domain enforcing token audience admits only tokens minted for one of its accepted audiences. The default
/// stays <c>bella-baxter</c> (what the CLI has always requested, so CLI-based CI jobs are ready with no change); an
/// operator who chose a unique audience sets it once per job or project.
/// </remarks>
public class OidcAudienceResolutionTests
{
    private static Func<string, string?> Env(string? value) =>
        name => name == WorkloadIdentityService.OidcAudienceVariable ? value : null;

    [Fact]
    public void The_flag_wins_then_the_environment_then_the_default()
    {
        Assert.Equal("flag", WorkloadIdentityService.ResolveOidcAudience("flag", Env("env")));
        Assert.Equal("env", WorkloadIdentityService.ResolveOidcAudience(null, Env("env")));
        Assert.Equal("bella-baxter", WorkloadIdentityService.ResolveOidcAudience(null, Env(null)));
    }

    [Fact]
    public void Blank_values_are_absent()
    {
        Assert.Equal("bella-baxter", WorkloadIdentityService.ResolveOidcAudience("  ", Env(" ")));
    }

    [Fact]
    public void Bella_run_passes_project_and_environment_into_the_right_parameters()
    {
        // Found while adding the audience: the call was positional, so the project landed in the TENANT parameter
        // and the environment in the PROJECT parameter — every keyless `bella run` resolved the wrong context.
        var context = RunCommand.WorkloadExchangeContext(new RunCommand.Settings { Project = "proj", Environment = "env" });

        Assert.Null(context.Tenant);
        Assert.Equal("proj", context.Project);
        Assert.Equal("env", context.Environment);
    }
}
