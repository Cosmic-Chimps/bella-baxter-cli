using BellaCli.Services;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// #824 — with refresh-token rotation and reuse revocation on the realm, presenting a refresh token
/// twice ends the login (measured against Keycloak 26.6: the client session is detached and even the
/// winner's new token is refused). These pin that the CLI never does, however its callers overlap.
/// </summary>
public class RefreshCoordinatorTests
{
    private sealed class Store
    {
        private readonly object _sync = new();
        private StoredTokens _tokens;
        public Store(string refreshToken) => _tokens = Tokens(refreshToken);
        public StoredTokens? Load() { lock (_sync) return _tokens; }
        public void Save(StoredTokens t) { lock (_sync) _tokens = t; }
    }

    private static StoredTokens Tokens(string refreshToken) =>
        new(AccessToken: $"at-{refreshToken}", RefreshToken: refreshToken, ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5));

    private static Task<IDisposable> NoLock(CancellationToken _) => Task.FromResult<IDisposable>(new MemoryStream());

    [Fact]
    public async Task Concurrent_callers_holding_the_same_token_send_ONE_refresh()
    {
        var coordinator = new RefreshCoordinator(new SemaphoreSlim(1, 1));
        var store = new Store("rt-0");
        var presented = new List<string>();
        var sequence = 0;

        async Task<StoredTokens> Exchange(StoredTokens current, CancellationToken ct)
        {
            lock (presented) presented.Add(current.RefreshToken);
            await Task.Delay(50, ct); // long enough for every other caller to be waiting
            var next = Tokens($"rt-{Interlocked.Increment(ref sequence)}");
            store.Save(next);
            return next;
        }

        // What `bella agent` does when several watches hit an expired token at once.
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            coordinator.RefreshAsync(store.Load, Exchange, NoLock, TestContext.Current.CancellationToken)));

        Assert.Equal(["rt-0"], presented);
        Assert.All(results, r => Assert.Equal("rt-1", r.RefreshToken));
    }

    [Fact]
    public async Task A_caller_whose_token_was_rotated_while_it_waited_sends_nothing()
    {
        var coordinator = new RefreshCoordinator(new SemaphoreSlim(1, 1));
        var store = new Store("rt-0");
        var exchanged = 0;

        // Another process rotates the stored token between this caller's decision and its turn.
        async Task<IDisposable> LockThatAnotherProcessHeld(CancellationToken _)
        {
            store.Save(Tokens("rt-from-another-process"));
            return await Task.FromResult<IDisposable>(new MemoryStream());
        }

        var result = await coordinator.RefreshAsync(
            store.Load,
            (_, _) => { exchanged++; return Task.FromResult(Tokens("never")); },
            LockThatAnotherProcessHeld,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exchanged);
        Assert.Equal("rt-from-another-process", result.RefreshToken);
    }

    [Fact]
    public async Task Sequential_refreshes_each_present_the_latest_token()
    {
        var coordinator = new RefreshCoordinator(new SemaphoreSlim(1, 1));
        var store = new Store("rt-0");
        var presented = new List<string>();
        var sequence = 0;

        Task<StoredTokens> Exchange(StoredTokens current, CancellationToken _)
        {
            presented.Add(current.RefreshToken);
            var next = Tokens($"rt-{++sequence}");
            store.Save(next);
            return Task.FromResult(next);
        }

        for (var i = 0; i < 3; i++)
            await coordinator.RefreshAsync(store.Load, Exchange, NoLock, TestContext.Current.CancellationToken);

        Assert.Equal(["rt-0", "rt-1", "rt-2"], presented);
    }

    [Fact]
    public async Task The_credential_lock_is_exclusive_until_released()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bella-lock-{Guid.NewGuid():N}");
        try
        {
            var first = await CredentialStore.AcquireExclusiveLockAsync(path, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            // While held, a second holder cannot get it (short timeout so the refusal is observable).
            await Assert.ThrowsAnyAsync<IOException>(() =>
                CredentialStore.AcquireExclusiveLockAsync(path, TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken));

            // Released, the next waiter proceeds.
            var waiter = CredentialStore.AcquireExclusiveLockAsync(path, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await Task.Delay(150, TestContext.Current.CancellationToken);
            Assert.False(waiter.IsCompleted);
            first.Dispose();
            (await waiter).Dispose();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
