using System.Text.Json;
using BellaCli.Infrastructure;

namespace BellaBaxter.Cli.Tests.Infrastructure;

/// <summary>
/// Issue #742 — in JSON mode, stdout carries the document and nothing else.
/// </summary>
/// <remarks>
/// <para>The bug was not a missing branch in two commands. Progress was written with
/// <c>AnsiConsole.Status()</c>, which bypasses <see cref="IOutputWriter"/> entirely and lands on
/// stdout, so <c>bella secrets get --json | jq .</c> read a progress line followed by the document
/// and failed — with stderr empty, giving a scripted caller nothing to distinguish this from a
/// malformed response. The pilot's Ansible role works around it by parsing only the last line.</para>
///
/// <para>There were <b>35</b> such call sites, not the two the issue names. So the fix is a rule in
/// the writer — a command says what it is doing, the writer decides whether saying so is allowed on
/// this stream — and these tests hold that rule from both ends: the behaviour here, and the source
/// guard in <see cref="NoDirectConsoleStatusTests"/> that stops the 36th being written.</para>
/// </remarks>
public class JsonModeOwnsStdoutTests
{
    /// <summary>Captures stdout and stderr around an action, restoring both afterwards.</summary>
    private static async Task<(string Out, string Err)> CaptureAsync(Func<Task> action)
    {
        var stdout = Console.Out;
        var stderr = Console.Error;
        var outBuf = new StringWriter();
        var errBuf = new StringWriter();
        try
        {
            Console.SetOut(outBuf);
            Console.SetError(errBuf);
            await action();
        }
        finally
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
        }

        return (outBuf.ToString(), errBuf.ToString());
    }

    [Fact]
    public async Task Stdout_parses_as_ONE_json_document_with_progress_in_flight()
    {
        // The reported reproduction, as a test: run a command that reports progress and then writes
        // its document, and pipe the result into a parser. This is the assertion the issue asks for —
        // "a test that pipes each command's --json output into a JSON parser" — because every other
        // formulation (no AnsiConsole call, stderr is non-empty) can pass while `jq` still fails.
        var writer = new JsonOutputWriter();

        var (stdout, _) = await CaptureAsync(async () =>
        {
            await writer.StatusAsync("Downloading secrets...", () => Task.CompletedTask);
            writer.WriteObject(new { apiKey = "value", other = "second" });
        });

        // Parses at all, and is exactly one document — a second would make this throw.
        var parsed = JsonDocument.Parse(stdout);
        Assert.Equal("value", parsed.RootElement.GetProperty("apiKey").GetString());

        Assert.Single(stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task The_progress_message_appears_on_NEITHER_stream()
    {
        // Dropped rather than redirected. A spinner is a terminal animation redrawn with control
        // characters; a machine reading stderr gains nothing from it, and stderr is where a scripted
        // caller looks when something is actually wrong.
        var writer = new JsonOutputWriter();

        var (stdout, stderr) = await CaptureAsync(async () =>
            await writer.StatusAsync("Downloading secrets...", () => Task.CompletedTask));

        Assert.DoesNotContain("Downloading", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Downloading", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_work_still_RUNS_and_its_result_is_still_awaited()
    {
        // The failure mode of "just drop the progress" is dropping the operation with it, or not
        // awaiting it — which would surface as an empty document rather than as an error.
        var writer = new JsonOutputWriter();
        var ran = false;

        await writer.StatusAsync("Working...", async () =>
        {
            await Task.Yield();
            ran = true;
        });

        Assert.True(ran, "StatusAsync must run and await the work, not merely accept it");
    }

    [Fact]
    public async Task A_failure_inside_the_work_still_propagates()
    {
        // If the wrapper swallowed, a command would report success with no document — the worst of
        // the available outcomes for a scripted caller, because exit 0 means "use this output".
        var writer = new JsonOutputWriter();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await writer.StatusAsync("Working...", () => throw new InvalidOperationException("boom")));
    }

    [Fact]
    public async Task Warnings_go_to_stderr_so_stdout_stays_parseable()
    {
        // Pre-existing behaviour, pinned here because it is half the contract: a warning that reached
        // stdout would break `jq` exactly as the progress line did.
        var writer = new JsonOutputWriter();

        var (stdout, stderr) = await CaptureAsync(async () =>
        {
            await writer.StatusAsync("Loading...", () => Task.CompletedTask);
            writer.WriteWarning("a secret was skipped");
            writer.WriteInfo("informational noise");
            writer.WriteObject(new { ok = true });
        });

        JsonDocument.Parse(stdout);
        Assert.Contains("skipped", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("informational noise", stdout, StringComparison.Ordinal);
    }
}
