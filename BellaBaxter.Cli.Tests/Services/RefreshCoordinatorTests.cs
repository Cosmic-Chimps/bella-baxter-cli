using System.Net;
using System.Net.Http.Json;
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
        const int callers = 8;

        // #1183: this waited a fixed 50 ms inside the exchange, assuming every other caller had decided to refresh
        // — read the token it meant to present — by then. Under a loaded full run the callers were not all started
        // within 50 ms; a late one first read rt-1, the token the exchange had just stored, and refreshed THAT. No
        // token was presented twice (the coordinator was right), but the test's premise — all callers holding rt-0
        // — had silently stopped being true. So the exchange now waits on that premise itself: every caller's
        // pre-gate read (the winner reads twice — once to decide, once under the gate — before it exchanges).
        var reads = 0;
        var everyCallerHasDecided = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        StoredTokens? Load()
        {
            if (Interlocked.Increment(ref reads) >= callers + 1)
                everyCallerHasDecided.TrySetResult();
            return store.Load();
        }

        async Task<StoredTokens> Exchange(StoredTokens current, CancellationToken ct)
        {
            lock (presented) presented.Add(current.RefreshToken);
            await everyCallerHasDecided.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            var next = Tokens($"rt-{Interlocked.Increment(ref sequence)}");
            store.Save(next);
            return next;
        }

        // What `bella agent` does when several watches hit an expired token at once.
        var results = await Task.WhenAll(Enumerable.Range(0, callers).Select(_ =>
            coordinator.RefreshAsync(Load, Exchange, NoLock, TestContext.Current.CancellationToken)));

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

    // ── #2002 — two `bella` PROCESSES starting at once ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A token endpoint as Keycloak runs it with <c>revokeRefreshToken</c> and <c>refreshTokenMaxReuse=0</c>: a refresh token is
    /// good ONCE, and presenting a spent one answers 400 invalid_grant and ends the whole session (every later refresh fails
    /// too, the winner's included: "Session doesn't have required client").
    /// </summary>
    private sealed class SingleUseTokenEndpoint : HttpMessageHandler
    {
        private readonly object _sync = new();
        private readonly HashSet<string> _spent = [];
        private int _issued;
        private bool _sessionDetached;
        public int Calls { get; private set; }
        public int Refusals { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var form = await request.Content!.ReadAsStringAsync(ct);
            var presented = Uri.UnescapeDataString(form.Split('&').Single(p => p.StartsWith("refresh_token=", StringComparison.Ordinal))["refresh_token=".Length..]);
            await Task.Delay(150, ct); // a round trip: the window in which an unserialised second process would also refresh

            lock (_sync)
            {
                Calls++;
                if (_sessionDetached || !_spent.Add(presented))
                {
                    _sessionDetached = true;
                    Refusals++;
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("""{"error":"invalid_grant","error_description":"Token reuse detected"}"""),
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($$"""{"refresh_token":"rt-{{++_issued}}"}"""),
                };
            }
        }
    }

    /// <summary>The stored login as the CLI keeps it: a file other processes read WITHOUT the lock and rewrite under it.</summary>
    private sealed class FileStore(string path)
    {
        public StoredTokens? Load() =>
            File.Exists(path) ? new StoredTokens($"at", File.ReadAllText(path), DateTimeOffset.UtcNow.AddMinutes(-1)) : null;

        public void Save(string refreshToken) => BellaCli.Infrastructure.PrivateFiles.WritePrivateAtomic(path, refreshToken);
    }

    [Fact]
    public async Task Two_processes_refreshing_at_once_send_ONE_refresh_and_both_get_the_new_login()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The private-file helpers are Unix-only here.");

        var dir = Path.Combine(Path.GetTempPath(), $"bella-race-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var lockPath = Path.Combine(dir, "tokens.lock");
            var store = new FileStore(Path.Combine(dir, "tokens.json"));
            store.Save("rt-0");
            var keycloak = new SingleUseTokenEndpoint();
            using var http = new HttpClient(keycloak);

            async Task<StoredTokens> Exchange(StoredTokens current, CancellationToken ct)
            {
                using var response = await http.PostAsync("https://auth.example.test/token",
                    new FormUrlEncodedContent(new Dictionary<string, string> { ["refresh_token"] = current.RefreshToken }), ct);
                response.EnsureSuccessStatusCode();
                var next = (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(ct)).GetProperty("refresh_token").GetString()!;
                store.Save(next);
                return store.Load()!;
            }

            // Each "process" has its OWN in-process gate and shares only the lock FILE, exactly as two `bella` processes do.
            var processes = Enumerable.Range(0, 2).Select(_ => new RefreshCoordinator(new SemaphoreSlim(1, 1))).ToArray();
            var results = await Task.WhenAll(processes.SelectMany(p => Enumerable.Range(0, 3).Select(_ =>
                Task.Run(() => p.RefreshAsync(
                    store.Load, Exchange, ct => CredentialStore.AcquireExclusiveLockAsync(lockPath, TimeSpan.FromSeconds(20), ct),
                    TestContext.Current.CancellationToken)))));

            Assert.Equal(1, keycloak.Calls);
            Assert.Equal(0, keycloak.Refusals);
            Assert.All(results, r => Assert.Equal("rt-1", r.RefreshToken));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task A_reader_never_sees_a_half_written_login_while_a_refresh_rewrites_it()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The private-file helpers are Unix-only here.");

        var dir = Path.Combine(Path.GetTempPath(), $"bella-torn-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "tokens.json");
            var a = new string('a', 256 * 1024);
            var b = new string('b', 256 * 1024);
            BellaCli.Infrastructure.PrivateFiles.WritePrivateAtomic(path, a);

            using var stop = new CancellationTokenSource();
            var writer = Task.Run(() =>
            {
                var flip = false;
                while (!stop.IsCancellationRequested)
                {
                    BellaCli.Infrastructure.PrivateFiles.WritePrivateAtomic(path, flip ? a : b);
                    flip = !flip;
                }
            }, TestContext.Current.CancellationToken);

            var torn = 0;
            for (var i = 0; i < 400; i++)
            {
                var read = File.ReadAllText(path); // what LoadTokens does, with no lock
                if (read != a && read != b)
                    torn++;
            }
            await stop.CancelAsync();
            await writer;

            Assert.Equal(0, torn);
            Assert.Empty(Directory.GetFiles(dir, "*.tmp")); // no temporary file is left behind
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
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
