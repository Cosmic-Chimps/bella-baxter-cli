namespace BellaCli.Services;

/// <summary>
/// #824 — makes an OAuth refresh safe under refresh-token ROTATION with reuse revocation.
///
/// <para>The <c>bella-baxter</c> realm now sets <c>revokeRefreshToken=true</c> with
/// <c>refreshTokenMaxReuse=0</c>, and those are REALM settings, so they govern this CLI's offline
/// session too. Under them a refresh token is spent the moment it is used, and presenting a spent one
/// is treated by Keycloak as theft: it DETACHES the client session, ending the whole login — measured
/// against Keycloak 26.6, a race between two refreshes of the same token left even the winner's new
/// token rejected with "Session doesn't have required client". So two refreshes must never present
/// the same token, and that happened in two ordinary ways:</para>
/// <list type="bullet">
///   <item><c>bella agent</c> polls several watches concurrently through one client; when the access
///   token expires each request's <c>TokenRefreshHandler</c> pre-flight refreshed on its own.</item>
///   <item>two <c>bella</c> processes (a script, <c>bella run</c> beside another command) share one
///   <c>tokens.json</c> and could refresh it at the same time.</item>
/// </list>
///
/// <para>So a refresh runs under an in-process gate AND a cross-process lock on the credential
/// directory, and re-reads the stored tokens once it holds both: if another caller already rotated
/// the token it was about to present, that caller's result IS this caller's result, and nothing is
/// sent.</para>
/// </summary>
internal sealed class RefreshCoordinator(SemaphoreSlim gate)
{
    /// <summary>The one gate every <see cref="AuthService"/> in this process shares.</summary>
    public static RefreshCoordinator Shared { get; } = new(new SemaphoreSlim(1, 1));

    /// <param name="load">Reads the stored tokens (fresh on every call).</param>
    /// <param name="exchange">Presents the given tokens' refresh token and stores the result.</param>
    /// <param name="acquireCrossProcessLock">Holds a lock other CLI processes honour.</param>
    public async Task<StoredTokens> RefreshAsync(
        Func<StoredTokens?> load,
        Func<StoredTokens, CancellationToken, Task<StoredTokens>> exchange,
        Func<CancellationToken, Task<IDisposable>> acquireCrossProcessLock,
        CancellationToken ct)
    {
        // What the caller was looking at when it decided to refresh.
        var seen = load() ?? throw new InvalidOperationException("No stored tokens to refresh.");

        await gate.WaitAsync(ct);
        try
        {
            using var _ = await acquireCrossProcessLock(ct);

            var current = load() ?? throw new InvalidOperationException("No stored tokens to refresh.");

            // Someone rotated while we waited: the token we meant to present is spent. Use theirs.
            if (!string.Equals(current.RefreshToken, seen.RefreshToken, StringComparison.Ordinal))
                return current;

            return await exchange(current, ct);
        }
        finally
        {
            gate.Release();
        }
    }
}
