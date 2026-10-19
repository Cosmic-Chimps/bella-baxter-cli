using System.Security.Cryptography;
using BellaCli.Commands.Exec;
using BellaCli.Commands.Issue;
using BellaCli.Commands.Run;
using BellaCli.Commands.Sdk;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console.Cli;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// Spec 085 (US2) — one command that signs in AND reads: the key it binds is the key it presents.
/// </summary>
/// <remarks>
/// <para>Before this, <c>bella run</c> on a runner exchanged its OIDC token for a key and then read secrets through a
/// client that presents a fresh EPHEMERAL key. On a tenant that requires registered devices that key matches nothing the
/// server recorded, so the read was refused — a keyless <c>bella run</c> could not work there at all.</para>
/// <para>So the keypair is made in memory by the auto-exchange, its public half is sent, the SAME key backs the client
/// that reads, and <c>exec</c> / <c>sdk run</c> hand the private half to their child. Nothing is written to disk: the
/// pair lives as long as the command, which is the whole reason a single command needs no exported secret.</para>
/// </remarks>
[Collection(ApiUrlEnvironmentCollection.Name)]
public class KeylessRunPresentsItsBoundKeyTests : KeylessFixture
{
    private static string SpkiOf(ECDiffieHellman key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    private static string SpkiOfPem(string pem)
    {
        using var ecdh = ECDiffieHellman.Create();
        ecdh.ImportPkcs8PrivateKey(Convert.FromBase64String(ZkeService.StripPemArmour(pem)), out _);
        return SpkiOf(ecdh);
    }

    private (string? Tenant, string? Project, string? Env) Context => ("acme", "p", "e");

    private static Dictionary<string, (long Length, DateTime Written)> Snapshot(string directory) =>
        !Directory.Exists(directory)
            ? []
            : Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .ToDictionary(f => f, f => (new FileInfo(f).Length, new FileInfo(f).LastWriteTimeUtc));

    // ── the auto-exchange sends a key and keeps its private half in memory ──────────────────────────────────

    [Fact]
    public async Task The_auto_exchange_sends_a_public_key_and_hands_back_the_matching_private_key()
    {
        var keyless = NewKeyless();

        var exchange = await keyless.TryAutoExchangeAsync("acme", "p", "e", ct: Ct);

        var exchanged = Assert.IsType<KeylessExchange.Exchanged>(exchange);
        using var result = exchanged.Key;
        var sent = Assert.Single(keyless.PublicKeys);
        DeviceKeypair.Validate(sent!);
        Assert.NotNull(result.DeviceKey);
        Assert.Equal(sent, SpkiOf(result.DeviceKey!)); // what was recorded is what will be presented
    }

    [Fact]
    public async Task Two_auto_exchanges_send_different_keys()
    {
        var keyless = NewKeyless();

        using var first = ((KeylessExchange.Exchanged)await keyless.TryAutoExchangeAsync("acme", "p", "e", ct: Ct)).Key;
        using var second = ((KeylessExchange.Exchanged)await keyless.TryAutoExchangeAsync("acme", "p", "e", ct: Ct)).Key;

        Assert.NotEqual(keyless.PublicKeys[0], keyless.PublicKeys[1]);
    }

    [Fact]
    public async Task Disposing_the_result_disposes_the_key()
    {
        var keyless = NewKeyless();
        var result = ((KeylessExchange.Exchanged)await keyless.TryAutoExchangeAsync("acme", "p", "e", ct: Ct)).Key;
        var key = result.DeviceKey!;

        result.Dispose();

        Assert.Throws<ObjectDisposedException>(() => key.ExportSubjectPublicKeyInfo());
    }

    [Fact]
    public async Task A_refused_exchange_names_the_servers_reason_and_leaves_no_key()
    {
        var keyless = NewKeyless();
        keyless.ExchangeThrows = new OidcExchangeRefusedException(new OidcExchangeRefusal(403, "device_key_required", "detail"));

        var exchange = await keyless.TryAutoExchangeAsync("acme", "p", "e", ct: Ct);

        var failed = Assert.IsType<KeylessExchange.Failed>(exchange);
        Assert.Equal("device_key_required", failed.Refusal?.Code);
    }

    // ── the exchange call itself ────────────────────────────────────────────────────────────────────────────

    /// <summary>Answers every request with one canned problem, and keeps the body it was sent.</summary>
    private sealed class CannedServer : IDisposable
    {
        private readonly System.Net.HttpListener _listener = new();
        private readonly Task _loop;
        public string Url { get; }
        public string? Body { get; private set; }

        public CannedServer(int status, string json)
        {
            int port;
            using (var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
            {
                probe.Start();
                port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            }

            Url = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    using (var reader = new StreamReader(context.Request.InputStream))
                        Body = await reader.ReadToEndAsync();
                    context.Response.StatusCode = status;
                    context.Response.ContentType = "application/problem+json";
                    await context.Response.OutputStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json));
                    context.Response.Close();
                }
                catch { /* closed */ }
            });
        }

        public void Dispose()
        {
            _listener.Close();
            try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { /* closing */ }
        }
    }

    [Theory]
    [InlineData(403, "device_key_required")]
    [InlineData(400, "device_key_malformed")]
    public async Task The_exchange_posts_the_public_key_and_the_servers_problem_is_surfaced_not_swallowed(int status, string code)
    {
        // The body the server really sends (`Results.Problem` with an `error` extension), through the real Kiota client.
        using var server = new CannedServer(
            status,
            $"{{\"type\":\"{code}\",\"title\":\"{code}\",\"status\":{status},\"detail\":\"say it\",\"error\":\"{code}\"}}");
        var config = new ConfigService(new BellaConfig(server.Url), Path.Combine(Dir, "config.json"));
        var service = new WorkloadIdentityService(new HttpClient(), config);

        var refused = await Assert.ThrowsAsync<OidcExchangeRefusedException>(() =>
            service.ExchangeGlobalAsync("oidc", "acme", "p", "e", Ct, publicKey: "the-public-key"));

        Assert.Equal(code, refused.Refusal.Code);
        Assert.Equal(status, refused.Refusal.Status);
        Assert.Equal("say it", refused.Refusal.Detail);
        Assert.Contains("\"publicKey\":\"the-public-key\"", server.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_problem_with_only_a_type_still_yields_a_code()
    {
        using var server = new CannedServer(403, "{\"type\":\"device_key_required\",\"status\":403}");
        var config = new ConfigService(new BellaConfig(server.Url), Path.Combine(Dir, "config.json"));

        var refused = await Assert.ThrowsAsync<OidcExchangeRefusedException>(() =>
            new WorkloadIdentityService(new HttpClient(), config).ExchangeGlobalAsync("oidc", "acme", "p", "e", Ct));

        Assert.Equal("device_key_required", refused.Refusal.Code);
    }

    // ── bella run reads with the bound key ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Run_presents_the_key_it_bound_and_never_an_ephemeral_one()
    {
        using var server = new PrivateKeyFromTheEnvironmentTests.StatusServer();
        var config = new ConfigService(new BellaConfig(server.Url), Path.Combine(Dir, "config.json"));
        var credentials = new FakeCredentials();
        var output = new RecordingOutput();
        var provider = new BellaClientProvider(config, credentials, new GlobalSettings(), new AuthService(config, credentials, new HttpClient()));
        var keyless = NewKeyless();
        keyless.IssuedKey = new OidcExchangeResult(
            "bax-0123456789abcdef0123456789abcdef-0123456789abcdef0123456789abcdef", DateTimeOffset.UtcNow.AddMinutes(10));
        var command = new RunCommand(provider, credentials, new ContextService(config, output, credentials), keyless, null!, output);

        await ((ICommand)command).ExecuteAsync(
            NoRemaining("run"),
            new RunCommand.Settings { Tenant = Context.Tenant, Project = Context.Project, Environment = Context.Env, Cmd = ["true"] },
            Ct);

        var bound = Assert.Single(keyless.PublicKeys);
        List<string?> presented;
        lock (server.PresentedKeys) presented = [.. server.PresentedKeys];
        Assert.NotEmpty(presented); // the command did call the server with its own client
        Assert.All(presented, key => Assert.Equal(bound, key));
    }

    [Fact]
    public void The_run_command_no_longer_builds_the_ephemeral_key_client_on_the_keyless_path()
    {
        var source = File.ReadAllText(Path.Combine(CliProject(), "Commands", "Run", "RunCommand.cs"));

        Assert.DoesNotContain("CreateClientWithApiKey(", source, StringComparison.Ordinal);
        Assert.Contains("CreateClientWithApiKeyAndZke(", source, StringComparison.Ordinal);
    }

    // ── exec and sdk run hand the child the private half ────────────────────────────────────────────────────

    private string ChildProbe => "printf '%s\\n' \"$BELLA_BAXTER_API_KEY\" > \"$BELLA_TEST_CHILD_OUT\"; "
        + "printf '%s' \"$BELLA_BAXTER_PRIVATE_KEY\" >> \"$BELLA_TEST_CHILD_OUT\"";

    private (string ApiKey, string Pem) ReadChildProbe()
    {
        var text = File.ReadAllText(Path.Combine(Dir, "child-out"));
        var split = text.IndexOf('\n');
        return (text[..split], text[(split + 1)..]);
    }

    [Fact]
    public async Task Exec_hands_its_child_the_api_key_and_the_matching_private_key_as_PEM()
    {
        var config = NewConfig();
        var credentials = new FakeCredentials();
        var keyless = NewKeyless();
        keyless.IssuedKey = new OidcExchangeResult("bax-issued-key", DateTimeOffset.UtcNow.AddMinutes(10));
        var command = new ExecCommand(
            credentials, config, new AuthService(config, credentials, new HttpClient()), keyless, new RecordingOutput());
        Env("BELLA_TEST_CHILD_OUT", Path.Combine(Dir, "child-out"));
        var before = Snapshot(DeviceKeypair.ConfigDirectory);

        var exit = await ((ICommand)command).ExecuteAsync(
            NoRemaining("exec"),
            new ExecCommand.Settings { Tenant = "acme", Project = "p", Environment = "e", Cmd = ["sh", "-c", ChildProbe] },
            Ct);

        Assert.Equal(0, exit);
        var (apiKey, pem) = ReadChildProbe();
        Assert.Equal("bax-issued-key", apiKey);
        Assert.StartsWith("-----BEGIN PRIVATE KEY-----", pem, StringComparison.Ordinal);
        Assert.Equal(keyless.PublicKeys.Single(), SpkiOfPem(pem));
        Assert.Equal(before, Snapshot(DeviceKeypair.ConfigDirectory)); // nothing written beside the CLI's credentials
    }

    [Fact]
    public async Task Sdk_run_hands_its_child_the_api_key_and_the_matching_private_key_as_PEM()
    {
        var config = NewConfig();
        var credentials = new FakeCredentials();
        var keyless = NewKeyless();
        keyless.IssuedKey = new OidcExchangeResult("bax-issued-key", DateTimeOffset.UtcNow.AddMinutes(10));
        var command = new SdkRunCommand(
            credentials, config, new AuthService(config, credentials, new HttpClient()), keyless, null!, new RecordingOutput());
        Env("BELLA_TEST_CHILD_OUT", Path.Combine(Dir, "child-out"));
        var before = Snapshot(DeviceKeypair.ConfigDirectory);

        var exit = await ((ICommand)command).ExecuteAsync(
            NoRemaining("sdk run"),
            new SdkRunCommand.Settings { Tenant = "acme", Project = "p", Environment = "e", Cmd = ["sh", "-c", ChildProbe] },
            Ct);

        Assert.Equal(0, exit);
        var (apiKey, pem) = ReadChildProbe();
        Assert.Equal("bax-issued-key", apiKey);
        Assert.StartsWith("-----BEGIN PRIVATE KEY-----", pem, StringComparison.Ordinal);
        Assert.Equal(keyless.PublicKeys.Single(), SpkiOfPem(pem));
        Assert.Equal(before, Snapshot(DeviceKeypair.ConfigDirectory));
    }

    [Fact]
    public async Task Sdk_run_passes_through_the_variable_a_previous_sign_in_step_exported()
    {
        // `bella auth oidc` exported BOTH variables; sdk run sees a bax- key from the environment, so it never
        // exchanges — and it used to strip BELLA_BAXTER_PRIVATE_KEY and put nothing back, un-doing the export.
        var config = NewConfig();
        var credentials = new EnvironmentKeyCredentials();
        var keyless = NewKeyless();
        var command = new SdkRunCommand(
            credentials, config, new AuthService(config, credentials, new HttpClient()), keyless, null!, new RecordingOutput());
        using var exported = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var pem = new string(PemEncoding.Write("PRIVATE KEY", exported.ExportPkcs8PrivateKey()));
        Env("BELLA_BAXTER_PRIVATE_KEY", pem);
        Env("BELLA_TEST_CHILD_OUT", Path.Combine(Dir, "child-out"));

        var exit = await ((ICommand)command).ExecuteAsync(
            NoRemaining("sdk run"),
            new SdkRunCommand.Settings { Cmd = ["sh", "-c", ChildProbe] },
            Ct);

        Assert.Equal(0, exit);
        Assert.Equal(pem, ReadChildProbe().Pem);
        Assert.Empty(keyless.Exchanges);
    }

    private sealed class EnvironmentKeyCredentials : CredentialStore
    {
        public override StoredTokens? LoadTokens() => null;

        public override StoredApiKey? LoadApiKey() =>
            new("bax-0123456789abcdef0123456789abcdef", "secret", "bax-0123456789abcdef0123456789abcdef-secret")
            {
                FromEnvironment = true,
            };
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
