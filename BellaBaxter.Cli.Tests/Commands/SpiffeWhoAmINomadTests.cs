using System.Text.Json;
using BellaCli.Commands.Spiffe;
using BellaCli.Infrastructure;

namespace BellaBaxter.Cli.Tests.Commands;

/// <summary>
/// Spec 081 T020 (FR-023; US3) — <c>bella spiffe whoami --node-type nomad</c> shows where the identity came from and
/// what it says, marked unverified, and never the token, in human and JSON output.
/// </summary>
public sealed class SpiffeWhoAmINomadTests : IDisposable
{
    private const string Signature = "U0lHTkFUVVJFLVNIT1VMRC1ORVZFUi1MRUFL";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bella-whoami-nomad-" + Guid.NewGuid().ToString("N"));

    public SpiffeWhoAmINomadTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static string B64(string json) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static readonly string Token =
        $"{B64("{\"alg\":\"RS256\"}")}.{B64(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["iss"] = "https://nomad.example.com", ["aud"] = "bella-spiffe/env", ["iat"] = 1_800_000_000, ["exp"] = 1_800_003_600,
            ["nomad_namespace"] = "default", ["nomad_job_id"] = "billing", ["nomad_task"] = "web", ["nomad_allocation_id"] = "alloc-1",
        }))}.{Signature}";

    private (int Exit, string All) Run(string path, bool json)
    {
        var output = new CapturingOutput();
        var command = new SpiffeWhoAmICommand(
            output, new GlobalSettings { OutputMode = json ? OutputMode.Json : OutputMode.Human }, new NoHttp());
        var exit = command.NomadAsync(new SpiffeWhoAmISettings { NodeType = "nomad", NomadIdentityPath = path, Json = json }, CancellationToken.None)
            .GetAwaiter().GetResult();
        return (exit, string.Join("\n", output.Lines));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void It_shows_source_issuer_audience_expiry_lifetime_and_claims_marked_unverified_but_never_the_token(bool json)
    {
        var path = Path.Combine(_dir, "nomad_bella.jwt");
        File.WriteAllText(path, Token);

        var (exit, all) = Run(path, json);

        Assert.Equal(0, exit);
        Assert.Contains(path, all, StringComparison.Ordinal);
        Assert.Contains("https://nomad.example.com", all, StringComparison.Ordinal);
        Assert.Contains("bella-spiffe/env", all, StringComparison.Ordinal);
        Assert.Contains("billing", all, StringComparison.Ordinal);
        Assert.Contains("alloc-1", all, StringComparison.Ordinal);
        Assert.Contains("3600", all, StringComparison.Ordinal);
        Assert.Contains(json ? "\"Unverified\":true" : "unverified", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Token, all, StringComparison.Ordinal);
        Assert.DoesNotContain(Signature, all, StringComparison.Ordinal);
        Assert.DoesNotContain(Token.Split('.')[1], all, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_missing_explicit_path_is_exit_1_and_names_the_path(bool json)
    {
        var (exit, all) = Run(Path.Combine(_dir, "absent.jwt"), json);

        Assert.Equal(1, exit);
        Assert.Contains("absent.jwt", all, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_is_not_a_jwt_is_exit_1()
    {
        var path = Path.Combine(_dir, "acl");
        File.WriteAllText(path, "11111111-2222-3333-4444-555555555555");

        var (exit, all) = Run(path, json: false);

        Assert.Equal(1, exit);
        Assert.DoesNotContain("11111111-2222", all, StringComparison.Ordinal);
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("whoami --node-type nomad makes no network call");
    }

    private sealed class CapturingOutput : IOutputWriter
    {
        public List<string> Lines { get; } = new();
        public Task StatusAsync(string message, Func<Task> work) => work();
        public Task StatusAsync(string message, Func<Action<string>, Task> work) => work(_ => { });
        public void WriteObject<T>(T obj) => Lines.Add(JsonSerializer.Serialize(obj));
        public void WriteList<T>(IEnumerable<T> items) => Lines.Add(JsonSerializer.Serialize(items));
        public void WriteTable(string[] headers, IEnumerable<string[]> rows) => Lines.Add(string.Join(",", rows.SelectMany(r => r)));
        public void WriteSuccess(string message) => Lines.Add(message);
        public void WriteError(string message, string? code = null) => Lines.Add(message);
        public void WriteWarning(string message) => Lines.Add(message);
        public void WriteInfo(string message) => Lines.Add(message);
    }
}
