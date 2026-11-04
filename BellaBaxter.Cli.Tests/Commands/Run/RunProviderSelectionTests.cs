using BellaBaxter.Cli.Tests.Helpers;
using BellaCli.Services;

namespace BellaBaxter.Cli.Tests.Commands.Run;

/// <summary>
/// #1616 — <c>bella run --provider &lt;slug&gt;</c> is declared but never read: the child receives the secrets of every
/// provider in the environment. Product decided (2026-10-30) to honour it: with <c>--provider</c>, <c>run</c> reads like
/// <c>bella secrets get --provider</c> (the provider route, <c>ProviderSecretValues.ReadAsync</c>) at start and on every
/// <c>--watch</c> reload, keeps the project globals underneath (the provider wins key by key), and refuses a slug that
/// is not attached or does not store secrets with the <see cref="SecretProviderSelection"/> text, before the child starts.
/// </summary>
/// <remarks>
/// <para><b>Zone: critical (TDD-mandatory, human review).</b> Which secrets reach a child process, under which token
/// scope, is secret-read authorisation; Security made "same scope on both routes" a mandatory condition.</para>
/// <para>Runs the REAL built <c>bella</c> (<see cref="BellaProcess"/>) with a dummy key from the environment against a
/// loopback fake API (<see cref="FakeBellaServer"/>) that serves the three routes as the server shapes them: the bulk
/// environment route (every provider merged over the globals, last one wins, <c>GetAllEnvironmentSecrets.cs:288,:331</c>),
/// the provider route and the project-globals route. The child is a script that records its environment.</para>
/// <para>Fixture: providers <c>vault-a</c> and <c>aws-b</c> store secrets, <c>rotator-c</c> is a certificate-rotation
/// provider. <c>COLLIDE_AB</c> is in both A and B (bulk: B's value, last wins); <c>SHARED_WITH_GLOBAL</c> is a project
/// global that A overrides.</para>
/// </remarks>
public class RunProviderSelectionTests
{
    /// <summary>A dummy key (not a real credential); the signing secret must be hex for the HMAC handler.</summary>
    private const string DummyKey =
        "bax-0123456789abcdef0123456789abcdef-00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";

    private const string Env = "/api/v1/projects/sandbox/environments/dev";

    private static bool OnUnix => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    private static readonly SecretProviderCandidate[] Attached =
    [
        new("vault-a", "HashiCorpVault", "Secrets"),
        new("aws-b", "AwsSecretsManager", "Secrets"),
        new("rotator-c", "Gigamon", "CertificateRotation"),
    ];

    private static string ProvidersJson() =>
        "[" + string.Join(",", Attached.Select((p, i) =>
            $$"""{"id":"{{i}}","providerId":"{{i}}","providerSlug":"{{p.Slug}}","providerName":"{{p.Slug}}","providerType":"{{p.Type}}","providerMetaType":"{{p.MetaType}}","assignedAt":"2026-10-30T00:00:00Z"}""")) + "]";

    private static string Items(params (string Key, string Value)[] items) =>
        "[" + string.Join(",", items.Select(i => $$"""{"key":"{{i.Key}}","value":"{{i.Value}}"}""")) + "]";

    private static string Globals(params (string Key, string Value)[] items) =>
        $$"""{"projectRef":"sandbox","projectSlug":"sandbox","secrets":{{Items(items)}}}""";

    private static string Bulk(long version, params (string Key, string Value)[] items) =>
        $$"""{"environmentSlug":"dev","environmentName":"Dev","version":{{version}},"secrets":{""" +
        string.Join(",", items.Select(i => $"\"{i.Key}\":\"{i.Value}\"")) + "}}";

    /// <summary>The two-provider environment. <paramref name="aOnly"/> is A's own key value (changed for a reload).</summary>
    private static FakeBellaServer TwoProviderServer(Func<string>? aOnly = null, Func<long>? version = null)
    {
        aOnly ??= () => "a";
        version ??= () => 100;
        return FakeBellaServer.WithSandboxDev()
            .On("GET", $"{Env}/providers", ProvidersJson())
            .On("GET", "/api/v1/projects/sandbox/secrets",
                Globals(("GLOBAL_ONLY", "g"), ("SHARED_WITH_GLOBAL", "from-global")))
            .On("GET", $"{Env}/providers/vault-a/secrets", () =>
                Items(("A_ONLY", aOnly()), ("SHARED_WITH_GLOBAL", "from-a"), ("COLLIDE_AB", "from-a")))
            .On("GET", $"{Env}/providers/aws-b/secrets", Items(("B_ONLY", "b"), ("COLLIDE_AB", "from-b")))
            // What the server merges today: globals, then A, then B; last one wins.
            .On("GET", $"{Env}/secrets", () => Bulk(version(),
                ("GLOBAL_ONLY", "g"), ("SHARED_WITH_GLOBAL", "from-a"), ("A_ONLY", aOnly()),
                ("COLLIDE_AB", "from-b"), ("B_ONLY", "b")))
            .On("GET", $"{Env}/secrets/version", () => $$"""{"environmentSlug":"dev","version":{{version()}}}""");
    }

    private static Dictionary<string, string> BellaEnvironment(FakeBellaServer server) => new()
    {
        ["BELLA_BAXTER_URL"] = server.Url,
        ["BELLA_BAXTER_API_KEY"] = DummyKey,
    };

    private static string[] Run(params string[] options) => ["run", "-p", "sandbox", "-e", "dev", .. options];

    // ── AC 1: A's secrets, not B's ──────────────────────────────────────────────────────────────────

    /// <summary>#1616 AC1 — with two providers that store secrets, <c>bella run --provider A -- env</c> gives the child
    /// A's secrets and not B's.</summary>
    [Fact]
    [Trait("Issue", "1616")]
    [Trait("Criterion", "AC1-only-the-named-provider")]
    [Trait("Zone", "critical")]
    public async Task Run_with_provider_gives_the_child_that_providers_secrets_and_not_the_others()
    {
        Assert.SkipUnless(OnUnix, "the recording child is a /bin/sh script");
        using var server = TwoProviderServer();
        using var bella = new BellaProcess();

        var (exit, stdout, stderr) = await bella.RunAsync(
            BellaEnvironment(server), [.. Run("--provider", "vault-a"), "--", .. bella.ChildCommand]);

        Assert.True(bella.ChildStarted, $"the child was not started (exit {exit}).\nstdout: {stdout}\nstderr: {stderr}");
        var env = bella.ChildEnvironment();
        Assert.Equal("a", env.GetValueOrDefault("A_ONLY"));
        Assert.False(env.ContainsKey("B_ONLY"), "the child received provider aws-b's secret B_ONLY under --provider vault-a");
        Assert.Equal("from-a", env.GetValueOrDefault("COLLIDE_AB")); // not B's value, which the merged read lets win
        Assert.Equal(0, exit);
    }

    // ── AC 2: globals underneath, the provider wins ─────────────────────────────────────────────────

    /// <summary>
    /// #1616 AC2 (Product's condition 1) — project globals reach the child; when A defines a key that is also a global,
    /// A wins. Guard: GREEN today, because the merged read already layers globals under the providers; it is here so
    /// the fix cannot satisfy AC1 by dropping the globals the app had without the option.
    /// </summary>
    [Fact]
    [Trait("Issue", "1616")]
    [Trait("Criterion", "AC2-globals-included-provider-wins")]
    [Trait("Zone", "critical")]
    public async Task Run_with_provider_keeps_the_project_globals_and_the_provider_overrides_them_key_by_key()
    {
        Assert.SkipUnless(OnUnix, "the recording child is a /bin/sh script");
        using var server = TwoProviderServer();
        using var bella = new BellaProcess();

        var (exit, stdout, stderr) = await bella.RunAsync(
            BellaEnvironment(server), [.. Run("--provider", "vault-a"), "--", .. bella.ChildCommand]);

        Assert.True(bella.ChildStarted, $"the child was not started (exit {exit}).\nstdout: {stdout}\nstderr: {stderr}");
        var env = bella.ChildEnvironment();
        Assert.Equal("g", env.GetValueOrDefault("GLOBAL_ONLY"));
        Assert.Equal("from-a", env.GetValueOrDefault("SHARED_WITH_GLOBAL"));
    }

    // ── AC 3: refusal before the child starts ───────────────────────────────────────────────────────

    /// <summary>
    /// #1616 AC3 — an unattached slug, or one that does not store secrets, is refused with the
    /// <see cref="SecretProviderSelection"/> text before the child starts (the expected text is computed from
    /// <c>SecretProviderSelection.Decide</c> itself, so the test follows that one wording).
    /// </summary>
    [Theory]
    [Trait("Issue", "1616")]
    [Trait("Criterion", "AC3-unattached-or-non-secrets-refused")]
    [Trait("Zone", "critical")]
    [InlineData("not-attached")]
    [InlineData("rotator-c")]
    public async Task Run_refuses_a_provider_that_is_not_attached_or_does_not_store_secrets_before_starting_the_child(
        string slug)
    {
        Assert.SkipUnless(OnUnix, "the recording child is a /bin/sh script");
        using var server = TwoProviderServer();
        using var bella = new BellaProcess();

        var (exit, stdout, stderr) = await bella.RunAsync(
            BellaEnvironment(server), [.. Run("--provider", slug), "--", .. bella.ChildCommand]);

        var expected = SecretProviderSelection.Decide(Attached, slug, canPrompt: false).Error;
        Assert.NotNull(expected);
        Assert.False(bella.ChildStarted, $"`bella run --provider {slug}` started the child");
        Assert.Contains(expected, BellaProcess.Said(stdout, stderr), StringComparison.Ordinal);
        Assert.NotEqual(0, exit);
    }

    // ── AC 4: --watch reloads through the same path ─────────────────────────────────────────────────

    /// <summary>
    /// #1616 AC4 — <c>--watch --provider A</c> reloads through the same path: after a version change the restarted
    /// child has A's NEW value and still none of B's secrets. (Restarting on a change in another provider is the
    /// documented limitation, not a failure; this test changes A, so it does not depend on it.)
    /// </summary>
    [Fact]
    [Trait("Issue", "1616")]
    [Trait("Criterion", "AC4-watch-reloads-through-the-provider-path")]
    [Trait("Zone", "critical")]
    public async Task Watch_with_provider_reloads_through_the_provider_route()
    {
        Assert.SkipUnless(OnUnix, "the recording child is a /bin/sh script");
        var generation = 1;
        using var server = TwoProviderServer(
            aOnly: () => generation == 1 ? "a" : "a2",
            version: () => generation == 1 ? 100 : 200);
        using var bella = new BellaProcess();

        async Task WaitForSnapshots(int count, TimeSpan within)
        {
            var until = DateTime.UtcNow + within;
            while (bella.ChildEnvironmentSnapshots().Count < count && DateTime.UtcNow < until)
                await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        await bella.RunAsync(
            BellaEnvironment(server),
            [.. Run("--provider", "vault-a", "--watch", "--poll-interval", "5"), "--", .. bella.LongRunningChildCommand],
            timeout: TimeSpan.FromSeconds(90),
            whileRunning: async process =>
            {
                try
                {
                    await WaitForSnapshots(1, TimeSpan.FromSeconds(30));
                    generation = 2; // A changes; the environment version moves
                    await WaitForSnapshots(2, TimeSpan.FromSeconds(30));
                }
                finally
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                }
            });

        var snapshots = bella.ChildEnvironmentSnapshots();
        Assert.True(snapshots.Count >= 2, $"--watch did not restart the child after the version changed ({snapshots.Count} start(s))");
        var reloaded = snapshots[1];
        Assert.Equal("a2", reloaded.GetValueOrDefault("A_ONLY")); // the reload happened, with A's new value
        Assert.False(reloaded.ContainsKey("B_ONLY"), "the --watch reload gave the child provider aws-b's secret B_ONLY");
        Assert.Equal("from-a", reloaded.GetValueOrDefault("COLLIDE_AB"));
    }

    // ── AC 5: scope (Security's condition) — the part a CLI test can hold ───────────────────────────

    /// <summary>
    /// #1616 AC5 (Security's condition 3), CLI half only. A scoped token admits key K1 of provider A and not K2 of the
    /// same provider: the server's provider route answers K1 only. The fake's bulk route answers K1 AND K2, i.e. it
    /// plays a route that does NOT apply the scope, which is exactly the divergence Security forbids. The child of
    /// <c>run --provider A</c> must get K1 only: the CLI must add nothing beyond what the provider route returned.
    /// </summary>
    /// <remarks>
    /// What this does NOT prove, and no CLI test can: that the SERVER applies the token's scope identically on the
    /// provider route, on the bulk route and on <c>secrets get --provider</c>. Scope is evaluated server-side; here it
    /// is only simulated by what the fake returns. That half needs a test in <c>BellaBaxter.Tests</c> with a really
    /// issued scoped key (<c>AuthenticatedApiFixture</c> + <c>ApiKeyCaller</c>) against both routes.
    /// </remarks>
    [Fact]
    [Trait("Issue", "1616")]
    [Trait("Criterion", "AC5-scope-negative-cli-half")]
    [Trait("Zone", "critical")]
    public async Task Run_with_provider_gives_a_scoped_token_nothing_the_provider_route_did_not_return()
    {
        Assert.SkipUnless(OnUnix, "the recording child is a /bin/sh script");
        using var server = FakeBellaServer.WithSandboxDev()
            .On("GET", $"{Env}/providers", ProvidersJson())
            .On("GET", "/api/v1/projects/sandbox/secrets", Globals())
            .On("GET", $"{Env}/providers/vault-a/secrets", Items(("K1", "in-scope")))
            .On("GET", $"{Env}/secrets", Bulk(100, ("K1", "in-scope"), ("K2", "out-of-scope")))
            .On("GET", $"{Env}/secrets/version", """{"environmentSlug":"dev","version":100}""");
        using var bella = new BellaProcess();

        var (exit, stdout, stderr) = await bella.RunAsync(
            BellaEnvironment(server), [.. Run("--provider", "vault-a"), "--", .. bella.ChildCommand]);

        Assert.True(bella.ChildStarted, $"the child was not started (exit {exit}).\nstdout: {stdout}\nstderr: {stderr}");
        var env = bella.ChildEnvironment();
        Assert.Equal("in-scope", env.GetValueOrDefault("K1"));
        Assert.False(env.ContainsKey("K2"), "run --provider gave the child K2, which the provider route (the token's scope) did not return");
    }
}
