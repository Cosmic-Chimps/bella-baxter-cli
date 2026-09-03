using System.Runtime.InteropServices;
using BellaCli.Services.Spiffe;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// Spec 001 T022 (US2) — where the agent listens, and who may read a private key from it.
/// </summary>
/// <remarks>
/// <para>These are security tests, not plumbing tests. The agent serves an SVID <b>and its private
/// key</b> over this socket, and it performs no workload attestation on the connection — it holds one
/// identity and gives it to whoever asks. So the filesystem permission is not a supporting control,
/// it is the entire authorisation boundary.</para>
///
/// <para>That is why this deliberately diverges from the spec's own research (R8) and
/// contracts/workload-api.md, both of which name SPIRE's default socket path. SPIRE can afford a
/// permissive socket because it attests the CALLER and answers per-caller; copying the default without
/// copying the attestation would be copying the risk alone.</para>
/// </remarks>
public class SvidSocketPathTests
{
    private static bool OnUnix => !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    // ── path resolution and precedence ───────────────────────────────────

    [Fact]
    public void An_explicit_socket_argument_wins()
    {
        // The operator's word is final. If they pass --socket, second-guessing it would make the flag
        // a suggestion.
        var location = SvidSocketPath.Resolve(
            explicitPath: "/run/custom/agent.sock",
            getEnvironmentVariable: _ => "/from/env/api.sock");

        Assert.Equal("/run/custom/agent.sock", location.Path);
        Assert.Equal(SvidSocketPathSource.Explicit, location.Source);
    }

    [Fact]
    public void The_SPIFFE_standard_variable_is_honoured()
    {
        // SPIFFE_ENDPOINT_SOCKET is the spec's actual portability mechanism — it is what standard
        // clients read. Honouring it is what buys go-spiffe/spiffe-helper compatibility WITHOUT
        // adopting a world-writable default path.
        var location = SvidSocketPath.Resolve(
            getEnvironmentVariable: name =>
                name == SvidSocketPath.SpiffeEndpointSocketVariable ? "/run/spiffe/api.sock" : null);

        Assert.Equal("/run/spiffe/api.sock", location.Path);
        Assert.Equal(SvidSocketPathSource.SpiffeEndpointSocket, location.Source);
    }

    [Fact]
    public void A_unix_scheme_prefix_is_stripped()
    {
        // Standard clients write unix:///run/x.sock. bind() takes a path, not a URI, and passing the
        // scheme through produces a socket literally named "unix:" in the current directory — which
        // then "works" locally and fails everywhere else.
        Assert.Equal("/run/spiffe/api.sock", SvidSocketPath.Normalize("unix:///run/spiffe/api.sock"));
        Assert.Equal("/run/spiffe/api.sock", SvidSocketPath.Normalize("/run/spiffe/api.sock"));
    }

    [Fact]
    public void The_DEFAULT_path_is_not_under_slash_tmp()
    {
        // /tmp is world-writable, so a hostile local process can pre-create the directory and own the
        // path the agent is about to bind. The spec named SPIRE's /tmp default; this is the deliberate
        // divergence, and the test states it so a future "align with the contract" change has to
        // confront the reason rather than just the inconsistency.
        var location = SvidSocketPath.Resolve(
            getEnvironmentVariable: _ => null,
            runtimeRoot: "/run/user/1000");

        Assert.Equal(SvidSocketPathSource.Default, location.Source);
        Assert.DoesNotContain("/tmp", location.Path, StringComparison.Ordinal);
        Assert.StartsWith("/run/user/1000", location.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void The_default_path_falls_back_when_there_is_no_runtime_dir()
    {
        // macOS and some containers have no XDG_RUNTIME_DIR. The home directory is per-user and not
        // world-writable, which is the property that matters.
        //
        // The /tmp assertion is repeated HERE deliberately: the test above passes an explicit
        // runtimeRoot, so it never exercises this fallback — and a mutation pointing the fallback at
        // /tmp/spire-agent/public passed the whole file. The guarantee has to be asserted on the path
        // that is actually taken when nothing is configured, which is this one.
        var location = SvidSocketPath.Resolve(getEnvironmentVariable: _ => null);

        Assert.Equal(SvidSocketPathSource.Default, location.Source);
        Assert.Contains(SvidSocketPath.DirectoryName, location.Path, StringComparison.Ordinal);
        Assert.EndsWith(SvidSocketPath.SocketFileName, location.Path, StringComparison.Ordinal);

        Assert.False(
            location.Path.StartsWith("/tmp", StringComparison.Ordinal),
            $"the fallback default must not live under world-writable /tmp, but was '{location.Path}'");
    }

    // ── the permission boundary ──────────────────────────────────────────

    [Fact]
    public void The_declared_modes_are_owner_only()
    {
        // Asserted on the CONSTANTS, so widening them is a test failure rather than a silent change.
        // A group-readable socket would let any process in the pod's group read the private key; a
        // world-readable one, any local user.
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, SvidSocketPath.SocketMode);

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            SvidSocketPath.DirectoryMode);

        // Explicitly: nothing for group or other, in either mode.
        foreach (var mode in new[] { SvidSocketPath.SocketMode, SvidSocketPath.DirectoryMode })
        {
            Assert.Equal(0, (int)(mode & (
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)));
        }
    }

    [Fact]
    public void PrepareDirectory_creates_the_directory_owner_only()
    {
        Assert.SkipUnless(OnUnix, "Unix permission bits do not describe the Windows ACL model.");

        var root = Path.Combine(Path.GetTempPath(), $"bella-sock-{Guid.NewGuid():N}");
        var socket = Path.Combine(root, "nested", "workload.sock");

        try
        {
            SvidSocketPath.PrepareDirectory(socket);

            var directory = Path.GetDirectoryName(socket)!;
            Assert.True(Directory.Exists(directory));

            // Created WITH the mode rather than created and then tightened: the tighten-afterwards
            // version has a window in which the path exists world-accessible.
            Assert.Equal(SvidSocketPath.DirectoryMode, File.GetUnixFileMode(directory));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PrepareDirectory_REFUSES_an_existing_world_accessible_directory()
    {
        Assert.SkipUnless(OnUnix, "Unix permission bits do not describe the Windows ACL model.");

        // Refuses rather than tightens. An already-open directory at that path is either a
        // misconfiguration or someone else's, and silently chmod'ing someone else's directory is a
        // worse answer than stopping — especially when the alternative reading is that a hostile
        // process created it to read what we are about to bind.
        var root = Path.Combine(Path.GetTempPath(), $"bella-sock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.SetUnixFileMode(root, SvidSocketPath.DirectoryMode | UnixFileMode.OtherRead
            | UnixFileMode.OtherExecute);

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => SvidSocketPath.PrepareDirectory(Path.Combine(root, "workload.sock")));

            Assert.Contains("beyond its owner", ex.Message, StringComparison.Ordinal);
            // The message must say what to DO, not just what is wrong.
            Assert.Contains("remove or", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PrepareDirectory_removes_a_stale_socket_file()
    {
        // A socket left by a killed agent makes bind() fail with "address already in use", which reads
        // like a port conflict and sends the operator looking for another process.
        var root = Path.Combine(Path.GetTempPath(), $"bella-sock-{Guid.NewGuid():N}");
        var socket = Path.Combine(root, "workload.sock");
        Directory.CreateDirectory(root);
        if (OnUnix) File.SetUnixFileMode(root, SvidSocketPath.DirectoryMode);
        File.WriteAllText(socket, "stale");

        try
        {
            SvidSocketPath.PrepareDirectory(socket);

            Assert.False(File.Exists(socket));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SecureSocket_applies_owner_only_to_the_bound_socket()
    {
        Assert.SkipUnless(OnUnix, "Unix permission bits do not describe the Windows ACL model.");

        var root = Path.Combine(Path.GetTempPath(), $"bella-sock-{Guid.NewGuid():N}");
        var socket = Path.Combine(root, "workload.sock");
        Directory.CreateDirectory(root);
        File.SetUnixFileMode(root, SvidSocketPath.DirectoryMode);

        // A regular file stands in for the bound socket: SetUnixFileMode does not care which it is,
        // and binding a real socket here would add a dependency on the loopback stack for no gain.
        File.WriteAllText(socket, string.Empty);
        File.SetUnixFileMode(socket, UnixFileMode.UserRead | UnixFileMode.UserWrite
            | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        try
        {
            SvidSocketPath.SecureSocket(socket);

            Assert.Equal(SvidSocketPath.SocketMode, File.GetUnixFileMode(socket));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
