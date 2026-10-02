using System.Runtime.InteropServices;
using BellaCli.Services.Spiffe;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// Spec 065 research R8 — the agent's re-attestation credential: owner-only, atomic, and shared by the X.509 and JWT
/// sources so that neither races the other into a lockout.
/// </summary>
public sealed class NodeReattestationCredentialStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bella-cred-" + Guid.NewGuid().ToString("N"));
    private static readonly Guid Env = Guid.NewGuid();

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void The_state_directory_resolves_flag_then_environment_then_XDG_then_home()
    {
        Func<string, string?> Env(string? agent, string? xdg) => n => n switch
        {
            AgentStateDirectory.EnvironmentVariable => agent,
            "XDG_STATE_HOME" => xdg,
            _ => null,
        };

        Assert.Equal("/flag", AgentStateDirectory.Resolve("/flag", Env("/env", "/xdg"), "/home"));
        Assert.Equal("/env", AgentStateDirectory.Resolve(null, Env("/env", "/xdg"), "/home"));
        Assert.Equal(Path.Combine("/xdg", "bella", "spiffe-agent"), AgentStateDirectory.Resolve(null, Env(null, "/xdg"), "/home"));
        Assert.Equal(Path.Combine("/home", ".local", "state", "bella", "spiffe-agent"), AgentStateDirectory.Resolve(null, Env(null, null), "/home"));
    }

    [Fact]
    public void A_stored_credential_is_owner_only_and_survives_a_new_store_instance()
    {
        var store = new NodeReattestationCredentialStore(_dir, Env, "billing");
        store.Store("bnr-first");

        Assert.Equal("bnr-first", new NodeReattestationCredentialStore(_dir, Env, "billing").Current);
        Assert.False(File.Exists(store.FilePath + ".tmp"), "the atomic write leaves no temp file behind");
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.FilePath));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(_dir));
        }
    }

    [Fact]
    public void A_workload_name_cannot_steer_the_file_out_of_the_state_directory()
    {
        var store = new NodeReattestationCredentialStore(_dir, Env, "../../etc/evil");

        Assert.Equal(_dir, Path.GetDirectoryName(store.FilePath));
    }

    [Fact]
    public async Task Concurrent_first_attestations_make_ONE_call_and_the_second_presents_the_first_ones_credential()
    {
        var store = new NodeReattestationCredentialStore(_dir, Env, "billing");
        var presented = new System.Collections.Concurrent.ConcurrentQueue<string?>();
        var calls = 0;

        async Task<(string, string?)> Attempt(string? credential)
        {
            presented.Enqueue(credential);
            var n = Interlocked.Increment(ref calls);
            await Task.Delay(50);
            return ("svid", credential is null && n == 1 ? "bnr-issued" : null);
        }

        await Task.WhenAll(store.AttestAsync(Attempt, CancellationToken.None), store.AttestAsync(Attempt, CancellationToken.None));

        Assert.Equal(2, calls);
        Assert.Equal(new string?[] { null, "bnr-issued" }, presented.ToArray());
        Assert.Equal("bnr-issued", store.Current);
    }

    [Fact]
    public async Task A_response_carrying_a_new_credential_replaces_the_held_one()
    {
        var store = new NodeReattestationCredentialStore(_dir, Env, "billing");
        store.Store("bnr-old");

        await store.AttestAsync<string>(_ => Task.FromResult(("svid", (string?)"bnr-restart")), CancellationToken.None);

        Assert.Equal("bnr-restart", store.Current);
    }

    [Fact]
    public async Task A_refusal_right_after_the_credential_changed_is_retried_exactly_once_with_the_new_one()
    {
        var store = new NodeReattestationCredentialStore(_dir, Env, "billing");
        store.Store("bnr-old");
        var presented = new List<string?>();

        var result = await store.AttestAsync<string>(credential =>
        {
            presented.Add(credential);
            if (credential == "bnr-old")
            {
                store.Store("bnr-new"); // the other source re-bound in the meantime
                throw new SvidAttestationException("refused");
            }
            return Task.FromResult(("svid", (string?)null));
        }, CancellationToken.None);

        Assert.Equal("svid", result);
        Assert.Equal(new string?[] { "bnr-old", "bnr-new" }, presented);
    }

    [Fact]
    public async Task A_refusal_with_an_unchanged_credential_is_not_retried()
    {
        var store = new NodeReattestationCredentialStore(_dir, Env, "billing");
        store.Store("bnr-held");
        var calls = 0;

        await Assert.ThrowsAsync<SvidAttestationException>(() => store.AttestAsync<string>(_ =>
        {
            calls++;
            throw new SvidAttestationException("refused");
        }, CancellationToken.None));

        Assert.Equal(1, calls);
    }
}
