using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using BellaBaxter.Crypto;
using BellaCli.Commands.Sdk;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// Issue #990 — <c>bella sdk run</c> with a stored <c>bax-</c> key hands the child the developer's device key ONLY when
/// the server confirms that key IS the one recorded against that API key.
/// </summary>
/// <remarks>
/// <para>The Go, Python, PHP, Ruby and Swift SDKs accept only a <c>bax-</c> key. Before this, a developer machine with a
/// stored key and a device file got a child with no device key and a refusal on its first secret read under enforcement.</para>
/// <para>An API key's only device is the one recorded at creation (spec 037 R5, spec 051), so injecting "the developer's"
/// key blindly would make the child present a key the gate refuses for THAT key. The CLI does not decide: it asks
/// <c>getZkeStatus</c> AS the API key, presenting the local device's public half, and the server answers with the same
/// gate the read would meet. Nothing is injected on any answer but a confirmed yes.</para>
/// <para>The suites below drive the real <see cref="SdkRunCommand"/>, the real selection and a real Kiota client against a
/// real listener, faking only the device file. They change the process's console, working directory and environment,
/// so they run alone.</para>
/// </remarks>
[Collection(ApiUrlEnvironmentCollection.Name)]
public class SdkRunDeviceKeyForApiKeyTests : KeylessFixture
{
    private const string KeyId = "0123456789abcdef0123456789abcdef";
    private const string SigningSecret = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";
    private const string ApiKey = $"bax-{KeyId}-{SigningSecret}";

    // ── a key, in the shapes this suite needs ───────────────────────────────────────────────────────────────

    private sealed record TestKey(string Pem, string Spki, byte[] Pkcs8)
    {
        public string Fingerprint => DeviceFingerprint.Compute(Convert.FromBase64String(Spki));

        public static TestKey New()
        {
            using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var pkcs8 = ecdh.ExportPkcs8PrivateKey();
            return new TestKey(
                new string(PemEncoding.Write("PRIVATE KEY", pkcs8)),
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

    private sealed class StubCredentials(StoredApiKey? apiKey) : CredentialStore
    {
        public override StoredTokens? LoadTokens() => null;
        public override StoredApiKey? LoadApiKey() => apiKey;
    }

    private sealed class LoginOnlyCredentials : CredentialStore
    {
        public override StoredTokens? LoadTokens() => Login("acme");
        public override StoredApiKey? LoadApiKey() => null;
    }

    private static StoredApiKey Stored() => new($"bax-{KeyId}", SigningSecret, ApiKey);

    /// <summary>What one request carried, as the server saw it.</summary>
    private sealed record Seen(string Path, string? PresentedKey, string? KeyId, string? Signature, bool SignatureValid);

    /// <summary>A real listener that answers <c>getZkeStatus</c> and records, per request, what authenticated it.</summary>
    private sealed class StatusServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;

        public bool Enforced { get; set; }
        public bool PresentedKeyRegistered { get; set; }

        /// <summary>When set, every answer carries this status and no body.</summary>
        public int? FailWith { get; set; }

        public List<Seen> Requests { get; } = [];
        public string Url { get; }

        public StatusServer(bool listen = true)
        {
            int port;
            using (var probe = new TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
            }

            Url = $"http://127.0.0.1:{port}";
            if (!listen)
            {
                _loop = Task.CompletedTask; // a refused connection: nothing is listening on that port
                return;
            }

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

                var request = context.Request;
                var keyId = request.Headers["X-Bella-Key-Id"];
                var signature = request.Headers["X-Bella-Signature"];
                var timestamp = request.Headers["X-Bella-Timestamp"];
                var path = request.Url!.AbsolutePath;
                var query = request.Url.Query.TrimStart('?');

                // Re-sign exactly as the server does, with the signing secret this suite gave the key.
                var emptyBody = Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant();
                using var hmac = new HMACSHA256(Convert.FromHexString(SigningSecret));
                var expected = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(
                    $"{request.HttpMethod}\n{path}\n{query}\n{timestamp}\n{emptyBody}"))).ToLowerInvariant();

                lock (Requests)
                    Requests.Add(new Seen(path, request.Headers["X-E2E-Public-Key"], keyId, signature, signature == expected));

                var isStatus = path.EndsWith("/tenants/me/zke", StringComparison.Ordinal);
                context.Response.StatusCode = FailWith ?? (isStatus ? 200 : 404);
                context.Response.ContentType = "application/json";
                var body = Encoding.UTF8.GetBytes(FailWith is null && isStatus
                    ? $"{{\"enforced\":{Enforced.ToString().ToLowerInvariant()},\"keyPresented\":true,"
                      + $"\"presentedKeyRegistered\":{PresentedKeyRegistered.ToString().ToLowerInvariant()}}}"
                    : "{\"title\":\"no\"}");
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            }
        }

        public List<Seen> Snapshot()
        {
            lock (Requests) return [.. Requests];
        }

        public int StatusCalls => Snapshot().Count(r => r.Path.EndsWith("/tenants/me/zke", StringComparison.Ordinal));

        public void Dispose()
        {
            if (_listener.IsListening)
                _listener.Close();
            try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { /* closing */ }
        }
    }

    /// <summary>Swaps the process's stderr and the Spectre console for buffers, and puts both back.</summary>
    private sealed class ConsoleCapture : IDisposable
    {
        private readonly TextWriter _originalError = Console.Error;
        private readonly IAnsiConsole _originalConsole = AnsiConsole.Console;
        private readonly StringWriter _error = new();
        private readonly StringWriter _out = new();

        public ConsoleCapture()
        {
            Console.SetError(_error);
            AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.No,
                ColorSystem = ColorSystemSupport.NoColors,
                Out = new AnsiConsoleOutput(_out),
                Interactive = InteractionSupport.No,
            });
            AnsiConsole.Console.Profile.Width = 4000; // never wrap a sentence the assertions look for
        }

        public string Error => _error.ToString();
        public string Out => _out.ToString();

        public void Dispose()
        {
            Console.SetError(_originalError);
            AnsiConsole.Console = _originalConsole;
        }
    }

    // ── harness ─────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record Run(int Exit, string? ChildKey, string? ChildApiKey, string Stderr, string Stdout);

    private string ChildOut => Path.Combine(Dir, "child-out");

    private const string ChildProbe =
        "printf 'api=%s\\nkey=%s\\n' \"$BELLA_BAXTER_API_KEY\" \"$BELLA_BAXTER_PRIVATE_KEY\" > \"$BELLA_TEST_CHILD_OUT\"";

    private async Task<Run> RunSdk(
        StatusServer server,
        TestKey? deviceFile,
        CredentialStore credentials,
        FakeKeyless? keyless = null,
        SdkRunCommand.Settings? settings = null)
    {
        var config = new ConfigService(new BellaConfig(server.Url), Path.Combine(Dir, "config.json"));
        var output = new RecordingOutput();
        var provider = new BellaClientProvider(
            config, credentials, new GlobalSettings(), new AuthService(config, credentials, new HttpClient()));
        var selection = new ZkeClientSelection(new FakeZke(deviceFile), provider, credentials, new DekLeaseCache(), output);
        var command = new SdkRunCommand(
            credentials, config, new AuthService(config, credentials, new HttpClient()),
            keyless ?? NewKeyless(WorkloadPlatform.None), selection, output);
        Env("BELLA_TEST_CHILD_OUT", ChildOut);
        settings ??= new SdkRunCommand.Settings();
        settings.Cmd = ["sh", "-c", ChildProbe];

        using var capture = new ConsoleCapture();
        var exit = await ((ICommand)command).ExecuteAsync(NoRemaining("sdk run"), settings, Ct);

        string? childKey = null, childApi = null;
        if (File.Exists(ChildOut))
        {
            var lines = File.ReadAllText(ChildOut).Split('\n');
            childApi = lines[0]["api=".Length..];
            childKey = string.Join('\n', lines.Skip(1))["key=".Length..].TrimEnd('\n');
        }

        return new Run(exit, string.IsNullOrEmpty(childKey) ? null : childKey, string.IsNullOrEmpty(childApi) ? null : childApi,
            capture.Error, capture.Out);
    }

    private static string SpkiOfPem(string pem)
    {
        using var ecdh = ECDiffieHellman.Create();
        ecdh.ImportPkcs8PrivateKey(Convert.FromBase64String(ZkeService.StripPemArmour(pem)), out _);
        return Convert.ToBase64String(ecdh.ExportSubjectPublicKeyInfo());
    }

    private static int Occurrences(string haystack, string needle) =>
        haystack.Split(needle).Length - 1;

    // ── a confirmed device key is injected ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_stored_key_and_a_device_file_the_server_confirms_hands_the_child_that_device_key_as_PEM()
    {
        using var server = new StatusServer { Enforced = true, PresentedKeyRegistered = true };
        var device = TestKey.New();

        var run = await RunSdk(server, device, new StubCredentials(Stored()));

        Assert.Equal(0, run.Exit);
        Assert.NotNull(run.ChildKey);
        Assert.StartsWith("-----BEGIN PRIVATE KEY-----", run.ChildKey, StringComparison.Ordinal);
        Assert.Equal(device.Spki, SpkiOfPem(run.ChildKey!)); // the device file's key, not another
        Assert.Equal(ApiKey, run.ChildApiKey);
        Assert.Contains("ZKE device key injected", run.Stdout);
        Assert.DoesNotContain(device.Pem, run.Stdout + run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_status_request_presents_the_device_public_key_and_is_signed_as_the_api_key()
    {
        using var server = new StatusServer { Enforced = true, PresentedKeyRegistered = true };
        var device = TestKey.New();

        await RunSdk(server, device, new StubCredentials(Stored()));

        var status = Assert.Single(server.Snapshot(), r => r.Path.EndsWith("/tenants/me/zke", StringComparison.Ordinal));
        Assert.Equal(device.Spki, status.PresentedKey);
        Assert.Equal(KeyId, status.KeyId);
        Assert.True(status.SignatureValid, "the status call must be HMAC-signed with the stored api key's secret");
    }

    [Fact]
    public async Task The_status_call_goes_only_to_the_origin_the_stored_key_is_bound_to()
    {
        // The key was stored for ANOTHER origin than the one this process is configured for: it must not be sent here.
        using var bound = new StatusServer { Enforced = true, PresentedKeyRegistered = true };
        using var configured = new StatusServer { Enforced = true, PresentedKeyRegistered = true };
        var stored = Stored() with { Origin = bound.Url };

        // A key bound elsewhere than config.json's server is refused before anything is sent; the refusal is the existing one.
        await Assert.ThrowsAsync<CredentialOriginException>(() => RunSdk(configured, TestKey.New(), new StubCredentials(stored)));

        Assert.Empty(configured.Snapshot());
        Assert.Empty(bound.Snapshot());
    }

    // ── anything but a confirmed yes injects nothing ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_device_the_server_does_not_recognise_for_this_key_is_not_injected_and_one_hint_names_the_fingerprint()
    {
        using var server = new StatusServer { Enforced = true, PresentedKeyRegistered = false };
        var device = TestKey.New();

        var run = await RunSdk(server, device, new StubCredentials(Stored()));

        Assert.Equal(0, run.Exit); // the child still runs; this is a hint, not a failure
        Assert.Null(run.ChildKey);
        Assert.Equal(ApiKey, run.ChildApiKey);
        Assert.Single(run.Stderr.TrimEnd().Split('\n')); // exactly one line
        Assert.Contains(device.Fingerprint, run.Stderr, StringComparison.Ordinal);
        Assert.Contains("BELLA_BAXTER_PRIVATE_KEY", run.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("ZKE device key injected", run.Stdout);
        Assert.DoesNotContain(device.Pem, run.Stdout + run.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(device.Spki, run.Stdout + run.Stderr, StringComparison.Ordinal); // the fingerprint, never the key
    }

    [Fact]
    public async Task A_device_that_is_not_recognised_on_a_tenant_that_enforces_nothing_says_nothing()
    {
        using var server = new StatusServer { Enforced = false, PresentedKeyRegistered = false };

        var run = await RunSdk(server, TestKey.New(), new StubCredentials(Stored()));

        Assert.Equal(0, run.Exit);
        Assert.Null(run.ChildKey);
        Assert.Equal("", run.Stderr);
        Assert.DoesNotContain("device key", run.Stdout);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(500)]
    public async Task A_status_call_that_fails_injects_nothing_and_leaves_one_dim_note_and_the_child_runs(int status)
    {
        using var server = new StatusServer { FailWith = status };
        var device = TestKey.New();

        var run = await RunSdk(server, device, new StubCredentials(Stored()));

        Assert.Equal(0, run.Exit);
        Assert.Null(run.ChildKey);
        Assert.Equal(ApiKey, run.ChildApiKey);
        Assert.Equal(1, Occurrences(run.Stdout, "could not confirm"));
        Assert.Contains("not injected", run.Stdout, StringComparison.Ordinal);
        Assert.Equal("", run.Stderr); // the note is not the refusal hint: nothing is known to be wrong
        Assert.DoesNotContain(device.Pem, run.Stdout + run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_server_that_cannot_be_reached_injects_nothing_and_leaves_one_dim_note_and_the_child_runs()
    {
        using var server = new StatusServer(listen: false);

        var run = await RunSdk(server, TestKey.New(), new StubCredentials(Stored()));

        Assert.Equal(0, run.Exit);
        Assert.Null(run.ChildKey);
        Assert.Equal(1, Occurrences(run.Stdout, "could not confirm"));
        Assert.Equal("", run.Stderr);
    }

    // ── nothing to confirm, nothing asked ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task No_device_file_means_no_status_call_and_no_noise()
    {
        using var server = new StatusServer { Enforced = true, PresentedKeyRegistered = true };

        var run = await RunSdk(server, null, new StubCredentials(Stored()));

        Assert.Equal(0, run.Exit);
        Assert.Null(run.ChildKey);
        Assert.Empty(server.Snapshot());
        Assert.Equal("", run.Stderr);
        Assert.DoesNotContain("device key", run.Stdout);
    }

    [Fact]
    public async Task A_key_already_exported_is_passed_through_unchanged_and_the_status_endpoint_is_not_called()
    {
        // Spec 085 behaviour, preserved: the exported variable says "this process is that device".
        using var server = new StatusServer { Enforced = true, PresentedKeyRegistered = true };
        var device = TestKey.New();
        var exported = TestKey.New();
        Env("BELLA_BAXTER_PRIVATE_KEY", exported.Pem);

        var run = await RunSdk(server, device, new StubCredentials(Stored()));

        Assert.Equal(0, run.Exit);
        Assert.Equal(exported.Pem, run.ChildKey);
        Assert.Empty(server.Snapshot());
    }

    [Fact]
    public async Task The_workload_identity_path_is_unchanged_and_never_asks_about_the_developers_device_file()
    {
        using var server = new StatusServer { Enforced = true, PresentedKeyRegistered = true };
        var device = TestKey.New();
        var keyless = NewKeyless(WorkloadPlatform.GitHubActions);
        keyless.IssuedKey = new OidcExchangeResult(ApiKey, DateTimeOffset.UtcNow.AddMinutes(10));

        var run = await RunSdk(
            server, device, new StubCredentials(null), keyless,
            new SdkRunCommand.Settings { Tenant = "acme", Project = "p", Environment = "e" });

        Assert.Equal(0, run.Exit);
        Assert.Single(keyless.Exchanges);
        Assert.Equal(keyless.PublicKeys.Single(), SpkiOfPem(run.ChildKey!)); // the key bound at the exchange, not the file
        Assert.NotEqual(device.Spki, SpkiOfPem(run.ChildKey!));
        Assert.Empty(server.Snapshot());
    }

    [Fact]
    public async Task The_developer_login_path_is_unchanged_and_makes_the_same_single_status_call_as_before()
    {
        using var server = new StatusServer { Enforced = true, PresentedKeyRegistered = true };
        var device = TestKey.New();

        var run = await RunSdk(
            server, device, new LoginOnlyCredentials(), settings: new SdkRunCommand.Settings { Project = "p", Environment = "e" });

        Assert.Equal(0, run.Exit);
        Assert.Equal(device.Spki, SpkiOfPem(run.ChildKey!));
        Assert.Equal(1, server.StatusCalls);
        Assert.All(server.Snapshot(), r => Assert.Null(r.KeyId)); // bearer, not HMAC: the key path is not involved
    }
}
