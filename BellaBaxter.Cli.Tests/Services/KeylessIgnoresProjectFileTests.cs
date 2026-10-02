using BellaCli.Services;
using BellaCli.Services.Spiffe;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// Spec 064 security review — a repository's <c>.bella</c> must not choose what the implicit keyless flows send.
/// </summary>
/// <remarks>
/// <c>bella run</c>/<c>exec</c>/<c>sdk run</c>/<c>auth oidc</c> post the token they find to the server the
/// <c>.bella</c> <c>url</c> names, and a <c>.bella</c> arrives with every repository you clone. When <c>.bella</c> could
/// also set <c>node_token_path</c>, a committed file flipped platform detection to Kubernetes and made those commands
/// read an arbitrary local file and send its raw contents there. Three rules close it: the keyless resolution ignores
/// <c>node_token_path</c>, only JWT-shaped content is sent, and <c>oidc_audience</c> is not read from <c>.bella</c>
/// at all (that last one is structural: the resolver has no project-file parameter).
/// </remarks>
public class KeylessIgnoresProjectFileTests
{
    private const string Jwt = "eyJhbGciOiJSUzI1NiJ9.eyJhdWQiOlsiYmVsbGEtYmF4dGVyIl0sImlzcyI6Imh0dHBzOi8vazhzIn0.c2ln";

    private static Func<string, string?> NoEnv => _ => null;

    private static Func<string, string?> BellaNames(string path) =>
        key => key == NodeTokenPath.ProjectFileKey ? path : null;

    [Fact]
    public void The_keyless_resolution_does_not_read_node_token_path_from_the_project_file()
    {
        var location = NodeTokenPath.ResolveForKeyless(NoEnv, BellaNames("/home/dev/.ssh/id_ed25519"), _ => false);

        Assert.Equal(NodeTokenPath.KubeletDefault, location.Path);
        Assert.Equal(NodeTokenPathSource.KubeletDefault, location.Source);
    }

    [Fact]
    public void The_agent_resolution_still_honours_it()
    {
        // Non-vacuous: the same reader makes Resolve answer from the project file, so the keyless test above is
        // proving an exclusion rather than a reader that never fires.
        var location = NodeTokenPath.Resolve(null, NoEnv, BellaNames("/pod/token"), _ => false);

        Assert.Equal("/pod/token", location.Path);
        Assert.Equal(NodeTokenPathSource.ProjectFile, location.Source);
    }

    [Fact]
    public void The_keyless_resolution_still_honours_the_environment_variable()
    {
        var location = NodeTokenPath.ResolveForKeyless(
            name => name == NodeTokenPath.EnvironmentVariable ? "/env/token" : null, BellaNames("/elsewhere"), _ => false);

        Assert.Equal("/env/token", location.Path);
    }

    [Fact]
    public void A_file_that_is_not_a_jwt_is_never_sent()
    {
        var location = new NodeTokenLocation("/home/dev/.ssh/id_ed25519", NodeTokenPathSource.Environment);

        var token = WorkloadIdentityService.ReadKubernetesToken(
            location, _ => true, _ => "-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEA\n");

        Assert.Null(token);
    }

    [Fact]
    public void A_jwt_is_sent_trimmed()
    {
        var location = new NodeTokenLocation(NodeTokenPath.Conventional, NodeTokenPathSource.Conventional);

        var token = WorkloadIdentityService.ReadKubernetesToken(location, p => p == NodeTokenPath.Conventional, _ => Jwt + "\n");

        Assert.Equal(Jwt, token);
    }

    [Fact]
    public void A_missing_token_is_null_not_a_throw()
    {
        var location = new NodeTokenLocation("/nope", NodeTokenPathSource.Environment);

        Assert.Null(WorkloadIdentityService.ReadKubernetesToken(location, _ => false, _ => throw new FileNotFoundException()));
    }
}
