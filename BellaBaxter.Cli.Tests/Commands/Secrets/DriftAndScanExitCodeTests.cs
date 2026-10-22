using BellaBaxter.Client.Models;
using BellaCli.Commands.Secrets;
using BellaCli.Infrastructure;

namespace BellaBaxter.Cli.Tests.Commands.Secrets;

/// <summary>
/// #1485 — <c>bella secrets drift</c> and <c>bella secrets scan</c> are documented as CI gates: "exits with code 1
/// when drift is detected" / when keys are not found. <c>--json</c> is the mode a pipeline actually uses, and it
/// returned 0 before the exit-code logic, so a job parsing the JSON went green with drift present.
///
/// <para>The contract is one exit code for a given result, whichever way it is printed. Both modes are driven
/// through the same code the command's <c>ExecuteAsync</c> ends with.</para>
/// </summary>
public class DriftAndScanExitCodeTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 2)]
    public void Drift_exits_one_exactly_when_keys_have_drifted_in_either_mode(bool json, int driftedKeys)
    {
        var output = new CapturingOutput();
        var drift = new DriftResponse
        {
            Environments = ["dev", "prod"],
            Secrets = [],
            Summary = new DriftSummary { DriftedKeys = driftedKeys, TotalKeys = 5 },
        };

        var exit = SecretsDriftCommand.Report(drift, "demo", json, output);

        Assert.Equal(driftedKeys > 0 ? 1 : 0, exit);
        // JSON mode still writes the document; the exit code does not replace it.
        Assert.Equal(json ? 1 : 0, output.Objects.Count);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void Scan_exits_one_exactly_when_a_key_is_not_found_in_either_mode(bool json, bool everyKeyFound)
    {
        var output = new CapturingOutput();
        var findings = new Dictionary<string, List<string>>
        {
            ["DATABASE_URL"] = ["app.py"],
            ["API_TOKEN"] = everyKeyFound ? ["settings.py"] : [],
        };

        var exit = SecretsScanCommand.Report(findings, "demo", "dev", "/tmp/src", fileCount: 3, usingGit: false, json, output);

        Assert.Equal(everyKeyFound ? 0 : 1, exit);
        Assert.Equal(json ? 1 : 0, output.Objects.Count);
    }

    private sealed class CapturingOutput : IOutputWriter
    {
        public List<object?> Objects { get; } = new();
        public Task StatusAsync(string message, Func<Task> work) => work();
        public Task StatusAsync(string message, Func<Action<string>, Task> work) => work(_ => { });
        public void WriteObject<T>(T obj) => Objects.Add(obj);
        public void WriteList<T>(IEnumerable<T> items) { }
        public void WriteTable(string[] headers, IEnumerable<string[]> rows) { }
        public void WriteSuccess(string message) { }
        public void WriteError(string message, string? code = null) { }
        public void WriteWarning(string message) { }
        public void WriteInfo(string message) { }
    }
}
