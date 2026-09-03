using System.Runtime.InteropServices;

namespace BellaCli.Services.Spiffe;

// Spec 001 T022 (US2) — where the agent listens, and who may connect.
//
// ─────────────────────────────────────────────────────────────────────────────────────────────────
// THE ONE DECISION THAT MATTERS, AND WHY IT DIFFERS FROM SPIRE
//
// The spec's research (R8) and contracts/workload-api.md both name SPIRE's default socket path,
// `/tmp/spire-agent/public/api.sock`. SPIRE also ships that socket world-accessible. Copying both
// would be a mistake, and the reason is worth stating in full because it is exactly the kind of thing
// that looks like harmless convention-following:
//
//   SPIRE can afford a permissive socket because SPIRE performs WORKLOAD ATTESTATION. When a process
//   connects, SPIRE reads the peer's credentials, inspects the process, and decides WHICH SVID that
//   particular caller is entitled to. The socket being open to all is fine because the ANSWER is
//   per-caller.
//
//   This agent does not do that. It holds ONE SVID and serves it to whoever asks. So the filesystem
//   permission is not a supporting control — it is the ENTIRE authorisation boundary. A world-readable
//   socket here means any local process, any other container in the pod sharing a mount, any user on a
//   shared host, can read a private key and impersonate the workload.
//
// So: owner-only (0600) on the socket, owner-only (0700) on its directory. Borrowing SPIRE's default
// without borrowing the attestation that justifies it would be borrowing the risk alone.
//
// AND NOT /tmp. `/tmp` is world-writable, so a hostile local process can pre-create the directory and
// win the race to own the path the agent is about to bind. The default lives under a per-user runtime
// directory instead; `$XDG_RUNTIME_DIR` where the platform provides it (already 0700 and per-user),
// falling back to the user's home.
//
// INTEROPERABILITY IS NOT LOST. The SPIFFE spec's actual portability mechanism is the
// `SPIFFE_ENDPOINT_SOCKET` environment variable, not a hardcoded path — that is what standard clients
// read. Honouring it gives go-spiffe/java-spiffe/spiffe-helper compatibility without a world-writable
// default, and an operator who genuinely wants the SPIRE path can still ask for it explicitly.
// ─────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Resolved listen address for the agent's local endpoint.</summary>
/// <param name="Path">Filesystem path to bind.</param>
/// <param name="Source">Where the path came from, for <c>bella spiffe status</c> and diagnostics.</param>
public sealed record SvidSocketLocation(string Path, SvidSocketPathSource Source);

/// <summary>How the socket path was chosen.</summary>
public enum SvidSocketPathSource
{
    /// <summary>Explicit <c>--socket</c> argument. The operator's word is final.</summary>
    Explicit,

    /// <summary>The SPIFFE standard environment variable, for client interoperability.</summary>
    SpiffeEndpointSocket,

    /// <summary>The safe per-user default.</summary>
    Default,
}

/// <summary>Chooses and secures the agent's local socket path.</summary>
public static class SvidSocketPath
{
    /// <summary>
    /// The SPIFFE-standard variable clients read to find a Workload API endpoint. Honoured so standard
    /// tooling interoperates; may carry a <c>unix://</c> prefix.
    /// </summary>
    public const string SpiffeEndpointSocketVariable = "SPIFFE_ENDPOINT_SOCKET";

    /// <summary>Socket file name under the chosen directory.</summary>
    public const string SocketFileName = "workload.sock";

    /// <summary>Directory name under the runtime root.</summary>
    public const string DirectoryName = "bella-spiffe";

    /// <summary>Owner read/write only — the socket serves a private key.</summary>
    public const UnixFileMode SocketMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Owner read/write/execute only, so nobody else can even list the directory.</summary>
    public const UnixFileMode DirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Resolves where to listen, in precedence order.</summary>
    /// <param name="explicitPath">A <c>--socket</c> value, if the operator gave one.</param>
    /// <param name="getEnvironmentVariable">Injected for testing.</param>
    /// <param name="runtimeRoot">Injected for testing; the per-user runtime directory.</param>
    public static SvidSocketLocation Resolve(
        string? explicitPath = null,
        Func<string, string?>? getEnvironmentVariable = null,
        string? runtimeRoot = null)
    {
        var env = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return new SvidSocketLocation(Normalize(explicitPath), SvidSocketPathSource.Explicit);
        }

        var fromSpiffe = env(SpiffeEndpointSocketVariable);
        if (!string.IsNullOrWhiteSpace(fromSpiffe))
        {
            // A client set this to tell us where it will look. Honouring it is the whole point of the
            // variable; second-guessing it would break the interoperability it exists for.
            return new SvidSocketLocation(Normalize(fromSpiffe), SvidSocketPathSource.SpiffeEndpointSocket);
        }

        var root = runtimeRoot
            ?? env("XDG_RUNTIME_DIR")
            // No XDG_RUNTIME_DIR (macOS, some containers). The home directory is per-user and not
            // world-writable, which is the property that matters; /tmp is neither.
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return new SvidSocketLocation(
            Path.Combine(root, DirectoryName, SocketFileName),
            SvidSocketPathSource.Default);
    }

    /// <summary>Strips a <c>unix://</c> scheme, which standard clients include and bind() does not accept.</summary>
    public static string Normalize(string path)
    {
        const string scheme = "unix://";
        var trimmed = path.Trim();

        return trimmed.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)
            ? trimmed[scheme.Length..]
            : trimmed;
    }

    /// <summary>
    /// Creates the socket's directory with owner-only permissions, and removes any stale socket file.
    /// </summary>
    /// <remarks>
    /// <para>The directory is created BEFORE binding and its mode set immediately, so there is no
    /// window in which the path exists with a permissive mode. Creating it permissively and tightening
    /// afterwards is the version of this that looks fine and races.</para>
    ///
    /// <para>Refuses when the directory already exists with a wider mode rather than silently
    /// tightening it: an existing world-writable directory at that path is either a misconfiguration
    /// or someone else's, and both deserve a stop rather than a repair. Windows is exempt — its ACL
    /// model is not the one these bits describe, and pretending otherwise would report false safety.</para>
    /// </remarks>
    public static void PrepareDirectory(string socketPath)
    {
        var directory = Path.GetDirectoryName(socketPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException(
                $"The socket path '{socketPath}' has no directory component.");
        }

        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        if (!Directory.Exists(directory))
        {
            if (isWindows)
            {
                Directory.CreateDirectory(directory);
            }
            else
            {
                // Created WITH the mode, not created then chmod'ed.
                Directory.CreateDirectory(directory, DirectoryMode);
            }
        }
        else if (!isWindows)
        {
            var mode = File.GetUnixFileMode(directory);
            var tooOpen = mode & ~DirectoryMode;

            if (tooOpen != 0)
            {
                throw new InvalidOperationException(
                    $"The socket directory '{directory}' is accessible beyond its owner ({mode}). "
                    + "The agent serves a private key over this socket and its permissions are the only "
                    + "thing restricting who may read it, so refusing rather than tightening: remove or "
                    + "fix the directory, then start the agent again.");
            }
        }

        // A leftover socket file from a killed process would make bind() fail with "address in use".
        // Deleting it is safe: a live agent holds no lock on the inode, and two agents on one path is
        // a configuration error we cannot resolve by guessing.
        if (File.Exists(socketPath))
        {
            File.Delete(socketPath);
        }
    }

    /// <summary>
    /// Applies owner-only permissions to a socket that has just been bound.
    /// </summary>
    /// <remarks>
    /// Called immediately after bind. The directory is already owner-only, so the window in which the
    /// socket exists with default permissions is not reachable by anyone else — belt and braces, in the
    /// order that makes the braces redundant rather than load-bearing.
    /// </remarks>
    public static void SecureSocket(string socketPath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        File.SetUnixFileMode(socketPath, SocketMode);
    }
}
