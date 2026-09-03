namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// Spec 001 T042/T044 — the two vendored copies of the SPIFFE Workload API schema must not drift.
/// </summary>
/// <remarks>
/// <para><b>Why there are two.</b> The CLI (which SERVES the API) and
/// <c>BellaBaxter.Spiffe.Client</c> (which CALLS it) each vendor
/// <c>Protos/workload.proto</c>. Both projects must build standalone when extracted to their own public
/// repositories, and a <c>.proto</c> cannot arrive through a <c>PackageReference</c> the way
/// <c>BellaBaxter.Client</c> does — so a shared file is not available to both.</para>
///
/// <para><b>Why this test is not optional.</b> A drifted schema is worse than no conformance test: the
/// server and the client would each be self-consistent, both would compile, the connection would
/// succeed, and the bytes would be misread. Nothing else in the suite can see that, because each side
/// only ever tests against its own copy.</para>
///
/// <para>The single permitted difference is <c>option csharp_namespace</c>, which is a code-generation
/// directive and touches no bytes on the wire.</para>
/// </remarks>
public class VendoredProtoDriftTests
{
    private const string CliProto = "Protos/workload.proto";
    private const string SdkProto = "Protos/workload.proto";

    [Fact]
    public void The_two_vendored_copies_differ_ONLY_in_the_csharp_namespace()
    {
        var cli = ReadSignificantLines(CliPath());
        var sdk = ReadSignificantLines(SdkPath());

        Assert.Equal(cli, sdk);
    }

    [Fact]
    public void Each_copy_still_declares_the_service_at_the_ROOT_with_no_package()
    {
        // Load-bearing: with no `package`, the gRPC method paths are `/SpiffeWorkloadAPI/...`. Adding
        // one would change every path so that no standard SPIFFE client finds the service — presenting
        // as "the socket is there but nothing answers", which is a long afternoon.
        foreach (var path in new[] { CliPath(), SdkPath() })
        {
            var text = File.ReadAllText(path);

            Assert.Contains("service SpiffeWorkloadAPI", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\npackage ", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Each_copy_declares_its_OWN_csharp_namespace()
    {
        // The one intended difference, asserted so that "they are identical" cannot be achieved by
        // accidentally giving both the same namespace — which would collide if a project ever
        // referenced both.
        Assert.Contains(
            "option csharp_namespace = \"BellaCli.Services.Spiffe.WorkloadApi\";",
            File.ReadAllText(CliPath()), StringComparison.Ordinal);

        Assert.Contains(
            "option csharp_namespace = \"BellaBaxter.Spiffe.Client.WorkloadApi\";",
            File.ReadAllText(SdkPath()), StringComparison.Ordinal);
    }

    /// <summary>
    /// Lines that affect the wire: comments, blank lines and the csharp_namespace option removed.
    /// </summary>
    /// <remarks>
    /// Comments are stripped because each copy carries a header explaining that it is one of two, which
    /// is exactly the sort of prose that SHOULD differ. Everything else — every message, field name,
    /// type and number — must match exactly.
    /// </remarks>
    private static List<string> ReadSignificantLines(string path) =>
        [.. File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Where(l => !l.StartsWith("//", StringComparison.Ordinal))
            .Where(l => !l.StartsWith("option csharp_namespace", StringComparison.Ordinal))];

    private static string CliPath() => Path.Combine(RepoRelative("apps/cli-dotnet/BellaBaxter.Cli"), CliProto);

    private static string SdkPath() =>
        Path.Combine(RepoRelative("apps/sdk/dotnet/BellaBaxter.Spiffe.Client"), SdkProto);

    /// <summary>Walks up from the test assembly to the repository root.</summary>
    /// <remarks>
    /// Anchored on a directory that only the repository root has, rather than on a fixed number of
    /// "../" hops: the latter breaks silently when the build output path changes, and the test then
    /// passes by never finding either file.
    /// </remarks>
    private static string RepoRelative(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "apps")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        var resolved = Path.Combine(directory!.FullName, relative);
        Assert.True(Directory.Exists(resolved), $"expected '{resolved}' to exist");
        return resolved;
    }
}
