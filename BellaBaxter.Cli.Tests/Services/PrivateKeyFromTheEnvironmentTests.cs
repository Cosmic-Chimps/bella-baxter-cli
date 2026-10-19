using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using BellaCli.Commands;
using BellaCli.Commands.Sdk;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console.Cli;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// Spec 085 (US2) — a later step that holds <c>BELLA_BAXTER_PRIVATE_KEY</c> is that device, and every command that reads
/// secrets agrees on it.
/// </summary>
/// <remarks>
/// <para>The order is explicit <c>--private-key</c>, then the variable, then the device file on this machine. It is
/// proven here through the one thing that cannot lie: the key the CLI PRESENTS on its status call
/// (<c>X-E2E-Public-Key</c>), read by a real listener, against a real client, through the real
/// <see cref="ZkeClientSelection"/>.</para>
/// <para>A variable that is set but cannot be read is a stated intention that failed, exactly like an unreadable
/// <c>--private-key</c>: under enforcement it stops, naming the variable; it never quietly becomes the device file.</para>
/// </remarks>
[Collection(ApiUrlEnvironmentCollection.Name)]
public class PrivateKeyFromTheEnvironmentTests : KeylessFixture
{
    private const string ApiKey = "bax-0123456789abcdef0123456789abcdef-0123456789abcdef0123456789abcdef0123456789abcdef";

    // ── a key, in every shape the CLI is asked to read ──────────────────────────────────────────────────────

    private sealed record TestKey(string Pem, string Base64Pkcs8, string Spki, byte[] Pkcs8)
    {
        public static TestKey New()
        {
            using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var pkcs8 = ecdh.ExportPkcs8PrivateKey();
            return new TestKey(
                new string(PemEncoding.Write("PRIVATE KEY", pkcs8)),
                Convert.ToBase64String(pkcs8),
                Convert.ToBase64String(ecdh.ExportSubjectPublicKeyInfo()),
                pkcs8);
        }
    }

    /// <summary>The key the device file would hold, without touching the real device file.</summary>
    private sealed class FakeZke(TestKey? device) : ZkeService
    {
        public override ECDiffieHellman? LoadEcdhKey()
        {
            if (device is null)
                return null;
            var ecdh = ECDiffieHellman.Create();
            ecdh.ImportPkcs8PrivateKey(device.Pkcs8, out _);
            return ecdh;
        }
    }

    private sealed class StubCredentials(StoredTokens? tokens, StoredApiKey? apiKey) : CredentialStore
    {
        public override StoredTokens? LoadTokens() => tokens;
        public override StoredApiKey? LoadApiKey() => apiKey;
    }

    /// <summary>A real HTTP listener that answers the ZKE status call and records the key each request presented.</summary>
    internal sealed class StatusServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;

        public bool Enforced { get; set; }
        public bool PresentedKeyRegistered { get; set; }
        public List<string?> PresentedKeys { get; } = [];
        public string Url { get; }

        public StatusServer()
        {
            int port;
            using (var probe = new TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
            }

            Url = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _loop = Task.Run(Loop);
        }

        private async Task Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch { return; }

                lock (PresentedKeys)
                    PresentedKeys.Add(context.Request.Headers["X-E2E-Public-Key"]);

                var isStatus = context.Request.Url!.AbsolutePath.EndsWith("/tenants/me/zke", StringComparison.Ordinal);
                context.Response.StatusCode = isStatus ? 200 : 404;
                context.Response.ContentType = "application/json";
                var body = Encoding.UTF8.GetBytes(isStatus
                    ? $"{{\"enforced\":{Enforced.ToString().ToLowerInvariant()},\"keyPresented\":true,"
                      + $"\"presentedKeyRegistered\":{PresentedKeyRegistered.ToString().ToLowerInvariant()}}}"
                    : "{\"title\":\"not found\"}");
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            }
        }

        public string? LastPresentedKey
        {
            get { lock (PresentedKeys) return PresentedKeys.LastOrDefault(); }
        }

        public void Dispose()
        {
            _listener.Close();
            try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { /* closing */ }
        }
    }

    // ── harness ─────────────────────────────────────────────────────────────────────────────────────────────

    private ConfigService ConfigFor(StatusServer server) =>
        new(new BellaConfig(server.Url), Path.Combine(Dir, "config.json"));

    /// <summary>A keyless-shaped caller: a <c>bax-</c> key from the environment, the way a CI step has one.</summary>
    private static StubCredentials KeylessCaller()
    {
        Env("BELLA_BAXTER_API_KEY", ApiKey);
        return new StubCredentials(null, new StoredApiKey("bax-0123456789abcdef0123456789abcdef", "secret", ApiKey) { FromEnvironment = true });
    }

    private ZkeClientSelection NewSelection(
        StatusServer server, TestKey? deviceFile, CredentialStore credentials, RecordingOutput output)
    {
        var config = ConfigFor(server);
        var provider = new BellaClientProvider(
            config, credentials, new GlobalSettings(), new AuthService(config, credentials, new HttpClient()));
        return new ZkeClientSelection(new FakeZke(deviceFile), provider, credentials, new DekLeaseCache(), output);
    }

    private string WritePemFile(TestKey key)
    {
        var path = Path.Combine(Dir, $"explicit-{Guid.NewGuid():N}.pem");
        File.WriteAllText(path, key.Pem);
        return path;
    }

    // ── the order ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_explicit_flag_beats_the_variable_which_beats_the_device_file()
    {
        using var server = new StatusServer();
        var device = TestKey.New();
        var fromEnvironment = TestKey.New();
        var explicitKey = TestKey.New();
        var selection = NewSelection(server, device, KeylessCaller(), new RecordingOutput());
        Env("BELLA_BAXTER_PRIVATE_KEY", fromEnvironment.Pem);

        var withFlag = await selection.SelectAsync("file://" + WritePemFile(explicitKey), null, false, Ct);
        Assert.False(withFlag.Stopped);
        Assert.Equal(explicitKey.Spki, server.LastPresentedKey);

        var withVariable = await selection.SelectAsync(null, null, false, Ct);
        Assert.False(withVariable.Stopped);
        Assert.Equal(fromEnvironment.Spki, server.LastPresentedKey);

        Environment.SetEnvironmentVariable("BELLA_BAXTER_PRIVATE_KEY", null);
        var withDeviceFile = await selection.SelectAsync(null, null, false, Ct);
        Assert.False(withDeviceFile.Stopped);
        Assert.Equal(device.Spki, server.LastPresentedKey);
    }

    [Fact]
    public async Task The_variable_may_hold_base64_PKCS8_as_well_as_PEM()
    {
        using var server = new StatusServer();
        var key = TestKey.New();
        var selection = NewSelection(server, null, KeylessCaller(), new RecordingOutput());
        Env("BELLA_BAXTER_PRIVATE_KEY", key.Base64Pkcs8);

        var result = await selection.SelectAsync(null, null, false, Ct);

        Assert.False(result.Stopped);
        Assert.True(result.DeviceKeyInUse);
        Assert.Equal(key.Spki, server.LastPresentedKey);
    }

    [Fact]
    public async Task A_variable_alone_is_enough_when_the_machine_has_no_device_file()
    {
        // The whole point: a CI runner has no `bella auth setup`, only the key the sign-in step exported.
        using var server = new StatusServer { Enforced = true, PresentedKeyRegistered = true };
        var key = TestKey.New();
        var selection = NewSelection(server, null, KeylessCaller(), new RecordingOutput());
        Env("BELLA_BAXTER_PRIVATE_KEY", key.Pem);

        var result = await selection.SelectAsync(null, null, false, Ct);

        Assert.False(result.Stopped);
        Assert.True(result.DeviceKeyInUse);
        Assert.Equal(key.Spki, server.LastPresentedKey);
    }

    // ── a variable that cannot be read ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("this is not a key")]
    [InlineData("-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----")]
    public async Task An_unreadable_variable_stops_under_enforcement_and_names_the_variable_even_with_a_device_file(string value)
    {
        using var server = new StatusServer { Enforced = true, PresentedKeyRegistered = true };
        var output = new RecordingOutput();
        var selection = NewSelection(server, TestKey.New(), KeylessCaller(), output);
        Env("BELLA_BAXTER_PRIVATE_KEY", value);

        var result = await selection.SelectAsync(null, null, false, Ct);

        // Never quietly the device file: the variable said "this process is that device", and it failed.
        Assert.True(result.Stopped);
        Assert.Equal(ZkeGate.RefusedExitCode, result.ExitCode);
        Assert.Contains("BELLA_BAXTER_PRIVATE_KEY", output.AllErrors);
        Assert.DoesNotContain("bella auth setup", output.AllErrors);
    }

    [Fact]
    public async Task An_unreadable_variable_on_a_tenant_that_enforces_nothing_falls_back_to_the_plain_client()
    {
        using var server = new StatusServer { Enforced = false };
        var output = new RecordingOutput();
        var selection = NewSelection(server, TestKey.New(), KeylessCaller(), output);
        Env("BELLA_BAXTER_PRIVATE_KEY", "this is not a key");

        var result = await selection.SelectAsync(null, null, false, Ct);

        Assert.False(result.Stopped);
        Assert.NotNull(result.Client);
        Assert.False(result.DeviceKeyInUse);
        Assert.Null(result.ExportPrivateKeyPem());
        Assert.Empty(output.Errors);
    }

    [Fact]
    public void The_gate_has_its_own_outcome_for_an_unreadable_variable()
    {
        Assert.Equal(
            ZkeGateOutcome.StopUnresolvableEnvironmentKey,
            ZkeGate.Decide(enforced: true, hasDeviceKey: true, overridePresent: false, overrideResolved: true,
                keyRegisteredHere: true, environmentKeyUnresolvable: true));
        Assert.Equal(
            ZkeGateOutcome.ProceedTransportOnly,
            ZkeGate.Decide(enforced: false, hasDeviceKey: true, overridePresent: false, overrideResolved: true,
                keyRegisteredHere: true, environmentKeyUnresolvable: true));
        Assert.True(ZkeGateOutcome.StopUnresolvableEnvironmentKey.Stops());
        Assert.Contains("BELLA_BAXTER_PRIVATE_KEY", ZkeGate.MessageFor(ZkeGateOutcome.StopUnresolvableEnvironmentKey));
    }

    // ── the advice for a keyless key ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_keyless_key_that_has_no_device_key_is_not_told_to_run_auth_setup()
    {
        using var server = new StatusServer { Enforced = true };
        var output = new RecordingOutput();
        var selection = NewSelection(server, null, KeylessCaller(), output);

        var result = await selection.SelectAsync(null, null, false, Ct);

        Assert.True(result.Stopped);
        Assert.DoesNotContain("bella auth setup", output.AllErrors);
        Assert.Contains("bella auth oidc", output.AllErrors);
        Assert.Contains("BELLA_BAXTER_PRIVATE_KEY", output.AllErrors);
    }

    [Fact]
    public async Task A_keyless_key_whose_device_is_not_registered_here_is_not_told_to_run_auth_setup()
    {
        using var server = new StatusServer { Enforced = true, PresentedKeyRegistered = false };
        var output = new RecordingOutput();
        var selection = NewSelection(server, null, KeylessCaller(), output);
        Env("BELLA_BAXTER_PRIVATE_KEY", TestKey.New().Pem);

        var result = await selection.SelectAsync(null, null, false, Ct);

        Assert.True(result.Stopped);
        Assert.DoesNotContain("bella auth setup", output.AllErrors);
        Assert.Contains("BELLA_BAXTER_PRIVATE_KEY", output.AllErrors);
    }

    [Fact]
    public async Task A_person_with_no_device_key_is_still_told_to_run_auth_setup()
    {
        using var server = new StatusServer { Enforced = true };
        var output = new RecordingOutput();
        var selection = NewSelection(server, null, new StubCredentials(Login("acme"), null), output);

        var result = await selection.SelectAsync(null, null, false, Ct);

        Assert.True(result.Stopped);
        Assert.Contains("bella auth setup", output.AllErrors);
    }

    [Fact]
    public void The_refusal_the_server_returns_anyway_is_not_told_to_run_auth_setup_for_a_keyless_key()
    {
        using var server = new StatusServer();
        var keylessOutput = new RecordingOutput();
        var personOutput = new RecordingOutput();
        var keyless = NewSelection(server, null, KeylessCaller(), keylessOutput);
        var person = NewSelection(server, null, new StubCredentials(Login("acme"), null), personOutput);
        var refusal = new InvalidOperationException("zke-required");

        Assert.True(keyless.RenderRefusal(refusal));
        Assert.True(person.RenderRefusal(refusal));

        Assert.DoesNotContain("bella auth setup", keylessOutput.AllErrors);
        Assert.Contains("BELLA_BAXTER_PRIVATE_KEY", keylessOutput.AllErrors);
        Assert.Contains("bella auth setup", personOutput.AllErrors);
    }

    // ── sdk run injects the key the gate selected ───────────────────────────────────────────────────────────

    [Fact]
    public async Task The_selection_hands_back_the_key_it_chose_as_PEM()
    {
        using var server = new StatusServer();
        var device = TestKey.New();
        var fromEnvironment = TestKey.New();
        var selection = NewSelection(server, device, KeylessCaller(), new RecordingOutput());

        var onDevice = await selection.SelectAsync(null, null, false, Ct);
        Assert.Equal(device.Spki, SpkiOfPem(onDevice.ExportPrivateKeyPem()!));

        Env("BELLA_BAXTER_PRIVATE_KEY", fromEnvironment.Pem);
        var onVariable = await selection.SelectAsync(null, null, false, Ct);
        Assert.Equal(fromEnvironment.Spki, SpkiOfPem(onVariable.ExportPrivateKeyPem()!));
    }

    [Fact]
    public async Task Sdk_run_gives_its_child_the_key_the_gate_used_not_always_the_device_file()
    {
        using var server = new StatusServer { Enforced = true, PresentedKeyRegistered = true };
        var device = TestKey.New();
        var fromEnvironment = TestKey.New();
        var credentials = new StubCredentials(Login("acme"), null);
        var config = ConfigFor(server);
        var output = new RecordingOutput();
        var selection = NewSelection(server, device, credentials, output);
        var keyless = NewKeyless(WorkloadPlatform.None);
        var command = new SdkRunCommand(
            credentials, config, new AuthService(config, credentials, new HttpClient()), keyless, selection, output);
        Env("BELLA_BAXTER_PRIVATE_KEY", fromEnvironment.Pem);
        var childOutput = Path.Combine(Dir, "child-key");
        Env("BELLA_TEST_CHILD_OUT", childOutput);

        var exit = await ((ICommand)command).ExecuteAsync(
            NoRemaining("sdk run"),
            new SdkRunCommand.Settings
            {
                Project = "p",
                Environment = "e",
                Cmd = ["sh", "-c", "printf '%s' \"$BELLA_BAXTER_PRIVATE_KEY\" > \"$BELLA_TEST_CHILD_OUT\""],
            },
            Ct);

        Assert.Equal(0, exit);
        Assert.Equal(fromEnvironment.Spki, SpkiOfPem(File.ReadAllText(childOutput)));
        Assert.Equal(fromEnvironment.Spki, server.LastPresentedKey); // the gate asked about the SAME key
    }

    [Fact]
    public void The_selection_keeps_the_source_strings_other_guards_read()
    {
        // ZkeRefusalNamesTheTenantTests and IssuePresentsTheDeviceKeyTests pin these by name; the new branches
        // must not have routed around them.
        var source = File.ReadAllText(Path.Combine(CliProject(), "Services", "ZkeClientSelection.cs"));

        Assert.Matches(new System.Text.RegularExpressions.Regex(@"ZkeGate\.MessageFor\(\s*outcome\s*,\s*\w"), source);
        Assert.Contains("LoadTokens()?.OrgSlug", source, StringComparison.Ordinal);
        Assert.Contains("env://BELLA_BAXTER_PRIVATE_KEY", source, StringComparison.Ordinal);
    }

    /// <summary>Compared by public half: two PEMs of one key need not be the same text on every platform.</summary>
    private static string SpkiOfPem(string pem)
    {
        using var ecdh = ECDiffieHellman.Create();
        ecdh.ImportPkcs8PrivateKey(Convert.FromBase64String(ZkeService.StripPemArmour(pem)), out _);
        return Convert.ToBase64String(ecdh.ExportSubjectPublicKeyInfo());
    }

    private static string CliProject()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "BellaBaxter.Cli")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "BellaBaxter.Cli");
    }
}
