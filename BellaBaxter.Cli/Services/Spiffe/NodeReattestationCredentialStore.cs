using BellaCli.Infrastructure;

namespace BellaCli.Services.Spiffe;

// Spec 065 (research R8) — the per-instance re-attestation credential an AWS-evidence attestation earns.
//
// WHY IT EXISTS. An EC2 instance identity document is public and never expires, so Bella binds an instance to its
// workload on first use and hands back a credential that every later attestation must present. Without it, anyone
// who has seen the document could attest as this instance. It is a secret, so it lives where only the agent's user
// can read it (PrivateFiles: directory 0700, file 0600, written atomically), and it survives an agent restart, so a
// restart needs no operator.
//
// ONE STORE, TWO SOURCES. The X.509 loop attests at startup and the JWT source attests on the first Workload API
// request. Without coordination, a JWT request during startup would race the first bind, and the loser would hold
// no credential and be refused as if forged. So an attestation that holds no credential runs under a single-flight
// lock: the second caller waits, then presents the credential the first one stored.

/// <summary>Where the agent keeps state that must survive a restart (contracts/cli.md).</summary>
public static class AgentStateDirectory
{
    public const string EnvironmentVariable = "BELLA_AGENT_STATE_DIR";

    public static string Resolve(string? explicitDir, Func<string, string?>? env = null, string? home = null)
    {
        var read = env ?? Environment.GetEnvironmentVariable;
        if (!string.IsNullOrWhiteSpace(explicitDir)) return explicitDir.Trim();
        if (read(EnvironmentVariable) is { Length: > 0 } fromEnv && !string.IsNullOrWhiteSpace(fromEnv)) return fromEnv.Trim();
        if (read("XDG_STATE_HOME") is { Length: > 0 } xdg && !string.IsNullOrWhiteSpace(xdg))
            return Path.Combine(xdg.Trim(), "bella", "spiffe-agent");
        var userHome = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userHome, ".local", "state", "bella", "spiffe-agent");
    }
}

/// <summary>Holds this instance's re-attestation credential for one (environment, workload).</summary>
public sealed class NodeReattestationCredentialStore
{
    private readonly SemaphoreSlim _firstAttestation = new(1, 1);
    private readonly object _io = new();

    public NodeReattestationCredentialStore(string stateDirectory, Guid environmentId, string workloadName)
    {
        StateDirectory = stateDirectory;
        FilePath = Path.Combine(stateDirectory, $"node-credential-{environmentId:D}-{Sanitise(workloadName)}");
    }

    public string StateDirectory { get; }
    public string FilePath { get; }

    /// <summary>The held credential, or null. Read fresh, so a credential stored by the other source is seen.</summary>
    public string? Current
    {
        get
        {
            lock (_io)
            {
                try
                {
                    return File.Exists(FilePath) ? File.ReadAllText(FilePath).Trim() is { Length: > 0 } c ? c : null : null;
                }
                catch (IOException)
                {
                    return null;
                }
                catch (UnauthorizedAccessException)
                {
                    return null;
                }
            }
        }
    }

    /// <summary>Stores a credential Bella issued, replacing any previous one. Atomic: written beside, then renamed.</summary>
    public void Store(string credential)
    {
        lock (_io)
        {
            PrivateFiles.EnsurePrivateDirectory(StateDirectory);
            var temp = FilePath + ".tmp";
            PrivateFiles.WritePrivate(temp, credential);
            File.Move(temp, FilePath, overwrite: true);
        }
    }

    /// <summary>
    /// Runs one attestation with the held credential, storing any credential the response carries. With no credential
    /// held, only one attestation runs at a time, and a waiting caller re-reads what the first one stored. A refusal
    /// right after the credential changed underneath us (the other source bound first) is retried exactly once.
    /// </summary>
    public async Task<T> AttestAsync<T>(
        Func<string?, Task<(T Result, string? Issued)>> attempt, CancellationToken ct)
    {
        var held = Current;
        if (held is not null)
            return await RunOnce(attempt, held).ConfigureAwait(false);

        await _firstAttestation.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await RunOnce(attempt, Current).ConfigureAwait(false);
        }
        finally
        {
            _firstAttestation.Release();
        }
    }

    private async Task<T> RunOnce<T>(Func<string?, Task<(T Result, string? Issued)>> attempt, string? presented)
    {
        try
        {
            var (result, issued) = await attempt(presented).ConfigureAwait(false);
            if (issued is not null) Store(issued);
            return result;
        }
        catch (SvidAttestationException) when (Current is { } now && now != presented)
        {
            var (result, issued) = await attempt(now).ConfigureAwait(false);
            if (issued is not null) Store(issued);
            return result;
        }
    }

    internal static string Sanitise(string name) =>
        new(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_').ToArray());
}
