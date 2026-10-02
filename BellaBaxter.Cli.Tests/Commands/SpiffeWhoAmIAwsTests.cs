using System.Text.Json;
using BellaBaxter.Cli.Tests.Services;
using BellaCli.Commands.Spiffe;
using BellaCli.Infrastructure;
using BellaCli.Services.Spiffe;

namespace BellaBaxter.Cli.Tests.Commands;

/// <summary>
/// Spec 065 FR-014 — <c>bella spiffe whoami --node-type aws-iid</c> reports what the agent would present, and never
/// prints the signature or the re-attestation credential.
/// </summary>
public sealed class SpiffeWhoAmIAwsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bella-whoami-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task It_reports_the_instance_and_whether_a_credential_is_held_but_never_the_secret_parts(bool json)
    {
        var envId = Guid.NewGuid();
        new NodeReattestationCredentialStore(_dir, envId, "billing").Store("bnr-secret-credential");
        var output = new CapturingOutput();
        var command = new SpiffeWhoAmICommand(
            output, new GlobalSettings { OutputMode = json ? OutputMode.Json : OutputMode.Human }, new Factory());

        var exit = await command.AwsAsync(new SpiffeWhoAmISettings
        {
            NodeType = "aws-iid", StateDir = _dir, EnvironmentId = envId.ToString(), WorkloadName = "billing", Json = json,
        }, CancellationToken.None);

        Assert.Equal(0, exit);
        var all = string.Join("\n", output.Lines);
        Assert.Contains("i-0abc", all);
        Assert.Contains("held", all);
        Assert.DoesNotContain("bnr-", all);
        Assert.DoesNotContain("c2lnbmF0dXJl", all); // the signature, in any form
    }

    private sealed class Factory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new AwsInstanceEvidenceTests.FakeImds().Client();
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
