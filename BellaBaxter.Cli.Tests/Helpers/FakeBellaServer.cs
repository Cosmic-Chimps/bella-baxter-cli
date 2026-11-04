using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using BellaBaxter.Crypto;

namespace BellaBaxter.Cli.Tests.Helpers;

/// <summary>
/// A loopback HTTP server that stands in for the Bella API when a test runs the REAL built <c>bella</c> as a
/// child process (see <see cref="BellaProcess"/>). It answers the routes a test registers, answers 404 for every
/// other one, and records EVERY request it receives, so a test can assert both what the CLI read and that it
/// made no request at all.
/// </summary>
/// <remarks>
/// Plain <c>http://127.0.0.1</c> is accepted by the CLI's https-or-loopback rule (backlog §2.31), so the
/// address can be passed as <c>BELLA_BAXTER_URL</c>. Routes match on method + absolute path, ignoring the query.
/// </remarks>
public sealed class FakeBellaServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<(string Method, string Path), Func<string>> _routes = new();
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public sealed record RecordedRequest(string Method, string Path, string Query);

    public FakeBellaServer()
    {
        Port = FreePort();
        Url = $"http://127.0.0.1:{Port}";
        _listener.Prefixes.Add(Url + "/");
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    public int Port { get; }

    /// <summary>The address to give the CLI as <c>BELLA_BAXTER_URL</c>.</summary>
    public string Url { get; }

    /// <summary>Every request received, in arrival order.</summary>
    public IReadOnlyList<RecordedRequest> Requests => [.. _requests];

    /// <summary>Answers <paramref name="method"/> <paramref name="path"/> with 200 and the JSON <paramref name="json"/>.</summary>
    public FakeBellaServer On(string method, string path, string json)
    {
        _routes[(method.ToUpperInvariant(), path)] = () => json;
        return this;
    }

    /// <summary>As <see cref="On(string,string,string)"/>, with a body computed per request (for watch-mode reloads).</summary>
    public FakeBellaServer On(string method, string path, Func<string> json)
    {
        _routes[(method.ToUpperInvariant(), path)] = json;
        return this;
    }

    /// <summary>
    /// A server that answers what <c>bella run -p sandbox -e dev</c> reads before it fetches secrets: the tenant's ZKE
    /// status (not enforced), the project and the environment. The secret routes are left to each test.
    /// </summary>
    public static FakeBellaServer WithSandboxDev() =>
        new FakeBellaServer()
            .On("GET", "/api/v1/tenants/me/zke",
                """{"enforced":false,"keyPresented":false,"presentedKeyRegistered":false}""")
            .On("GET", "/api/v1/projects/sandbox",
                """{"id":"11111111-1111-1111-1111-111111111111","name":"Sandbox","slug":"sandbox","status":"Active","members":{}}""")
            .On("GET", "/api/v1/projects/sandbox/environments/dev",
                """{"id":"22222222-2222-2222-2222-222222222222","slug":"dev","name":"Dev","status":"Active"}""");

    public int CountOf(string method, string path) =>
        _requests.Count(r => r.Method == method.ToUpperInvariant() && r.Path == path);

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch
            {
                return; // listener stopped
            }

            var method = ctx.Request.HttpMethod.ToUpperInvariant();
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            _requests.Enqueue(new RecordedRequest(method, path, ctx.Request.Url?.Query ?? ""));

            try
            {
                if (_routes.TryGetValue((method, path), out var body))
                {
                    var bytes = Encoding.UTF8.GetBytes(Envelope(path, ctx.Request.Headers["X-E2E-Public-Key"], body()));
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                }
                else
                {
                    var bytes = Encoding.UTF8.GetBytes("{\"title\":\"Not Found\",\"status\":404}");
                    ctx.Response.StatusCode = 404;
                    ctx.Response.ContentType = "application/problem+json";
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                }
            }
            catch
            {
                // client went away; nothing to assert on
            }
            finally
            {
                try { ctx.Response.Close(); } catch { /* best effort */ }
            }
        }
    }

    private static readonly JsonSerializerOptions Wire = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// As the server does (apps/sdk/SDK_CONTRACT.md, "Which Endpoints Support E2EE"): a value-carrying
    /// <c>/secrets</c> read answered to a presented <c>X-E2E-Public-Key</c> is an ECIES envelope. The CLI refuses a
    /// plain answer on those reads (#1050), so the fake must encrypt them. <c>…/secrets/version</c> stays plain.
    /// </summary>
    private static string Envelope(string path, string? clientPublicKey, string json)
    {
        if (string.IsNullOrEmpty(clientPublicKey)
            || !path.Contains("/secrets", StringComparison.Ordinal)
            || path.EndsWith("/secrets/version", StringComparison.Ordinal))
            return json;
        return JsonSerializer.Serialize(EciesAlgorithm.Encrypt(Encoding.UTF8.GetBytes(json), clientPublicKey), Wire);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch { /* best effort */ }
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { /* best effort */ }
    }
}
