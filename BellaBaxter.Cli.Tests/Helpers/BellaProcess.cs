using System.Diagnostics;

namespace BellaBaxter.Cli.Tests.Helpers;

/// <summary>
/// Runs the REAL built <c>bella</c> (<c>bella.dll</c>, copied next to the tests by the project reference) as a child
/// process, the way <c>UntrustedServerExitContractTests</c> does: no terminal (stdin closed, stdout/stderr piped), an
/// isolated <c>HOME</c> so the user's <c>config.json</c> and credentials are never read or written, and none of the
/// <c>BELLA_*</c>/CI variables of the machine running the tests.
/// </summary>
/// <remarks>
/// <para>It also provides a recording CHILD: <see cref="ChildCommand"/> is a <c>/bin/sh</c> script that writes the
/// argv it received to <see cref="ChildArgsFile"/> (one per line) and its environment to <see cref="ChildEnvFile"/>.
/// Neither file existing after a run is the proof that no child process was started.</para>
/// <para>Unix only (the child is a shell script); every caller skips on Windows.</para>
/// </remarks>
public sealed class BellaProcess : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bella-proc-" + Guid.NewGuid().ToString("N"));

    public BellaProcess()
    {
        Home = Path.Combine(_root, "home");
        WorkDir = Path.Combine(_root, "work");
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(WorkDir);

        ChildScript = Path.Combine(_root, "child.sh");
        ChildArgsFile = Path.Combine(_root, "child.args");
        ChildEnvFile = Path.Combine(_root, "child.env");
        File.WriteAllText(ChildScript,
            "#!/bin/sh\n"
            + $": > '{ChildArgsFile}'\n"
            + $"for a in \"$@\"; do printf '%s\\n' \"$a\" >> '{ChildArgsFile}'; done\n"
            + $"env > '{ChildEnvFile}'\n");
    }

    /// <summary>
    /// A child that stays alive (for <c>--watch</c>): each start writes its environment to a new numbered snapshot
    /// (<c>snap.0.env</c>, <c>snap.1.env</c>, …) and then sleeps until it is killed.
    /// </summary>
    public string[] LongRunningChildCommand
    {
        get
        {
            var script = Path.Combine(_root, "long-child.sh");
            if (!File.Exists(script))
            {
                File.WriteAllText(script,
                    "#!/bin/sh\n"
                    + $"n=$(ls '{_root}' | grep -c '^snap\\.[0-9]*\\.env$')\n"
                    + $"env > '{_root}/snap.tmp.'$$\n"
                    + $"mv '{_root}/snap.tmp.'$$ '{_root}/snap.'$n'.env'\n"
                    + "exec sleep 120\n");
            }
            return ["/bin/sh", script];
        }
    }

    /// <summary>The environment of each start of <see cref="LongRunningChildCommand"/>, in start order.</summary>
    public List<Dictionary<string, string>> ChildEnvironmentSnapshots()
    {
        var snapshots = new List<Dictionary<string, string>>();
        for (var n = 0; File.Exists(Path.Combine(_root, $"snap.{n}.env")); n++)
            snapshots.Add(ParseEnv(File.ReadAllLines(Path.Combine(_root, $"snap.{n}.env"))));
        return snapshots;
    }

    public string Home { get; }
    public string WorkDir { get; }
    public string ChildScript { get; }
    public string ChildArgsFile { get; }
    public string ChildEnvFile { get; }

    /// <summary>The recording child, as argv tokens to put after <c>--</c> (or wherever the test needs them).</summary>
    public string[] ChildCommand => ["/bin/sh", ChildScript];

    public bool ChildStarted => File.Exists(ChildArgsFile) || File.Exists(ChildEnvFile);

    /// <summary>The argv the child received after the script name, in order.</summary>
    public string[] ChildArgs =>
        File.Exists(ChildArgsFile)
            ? File.ReadAllLines(ChildArgsFile)
            : throw new InvalidOperationException("the child was not started");

    /// <summary>The child's environment (KEY → VALUE; multi-line values are not expected in these tests).</summary>
    public Dictionary<string, string> ChildEnvironment()
    {
        if (!File.Exists(ChildEnvFile))
            throw new InvalidOperationException("the child was not started");
        return ParseEnv(File.ReadAllLines(ChildEnvFile));
    }

    /// <summary>
    /// What the CLI said, for matching on text: stdout and stderr as they are, plus the <c>error</c> field of every
    /// JSON line (the CLI writes errors as JSON on stderr when there is no terminal, which escapes quotes).
    /// </summary>
    public static string Said(string stdout, string stderr)
    {
        var text = new System.Text.StringBuilder().AppendLine(stdout).AppendLine(stderr);
        foreach (var line in (stdout + "\n" + stderr).Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith('{')) continue;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(trimmed);
                foreach (var name in new[] { "error", "message", "warning" })
                    if (doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
                        text.AppendLine(v.GetString());
            }
            catch (System.Text.Json.JsonException) { /* not JSON */ }
        }
        return text.ToString();
    }

    private static Dictionary<string, string> ParseEnv(IEnumerable<string> lines)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var idx = line.IndexOf('=');
            if (idx > 0)
                map[line[..idx]] = line[(idx + 1)..];
        }
        return map;
    }

    public async Task<(int Exit, string Stdout, string Stderr)> RunAsync(
        IReadOnlyDictionary<string, string> environment,
        IEnumerable<string> args,
        TimeSpan? timeout = null,
        Func<Process, Task>? whileRunning = null)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "bella.dll");
        Assert.True(File.Exists(dll), $"{dll} is not next to the tests; the CLI project reference should copy it.");

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = WorkDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(dll);
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        foreach (var name in psi.Environment.Keys.ToList())
        {
            if (name.StartsWith("BELLA_", StringComparison.Ordinal)
                || name.StartsWith("BAXTER_", StringComparison.Ordinal)
                || name.StartsWith("ACTIONS_", StringComparison.Ordinal)
                || name.StartsWith("GITHUB_", StringComparison.Ordinal)
                || name.StartsWith("SYSTEM_", StringComparison.Ordinal)
                || name.StartsWith("KUBERNETES_", StringComparison.Ordinal)
                || name is "CI" or "GITLAB_CI" or "TF_BUILD" or "BUILD_ID" or "CODEBUILD_BUILD_ID"
                    or "GOOGLE_CLOUD_PROJECT")
                psi.Environment.Remove(name);
        }
        psi.Environment["HOME"] = Home;
        psi.Environment["USERPROFILE"] = Home;
        psi.Environment["XDG_CONFIG_HOME"] = Path.Combine(Home, ".config");
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        foreach (var (name, value) in environment)
            psi.Environment[name] = value;

        using var process = Process.Start(psi)!;
        process.StandardInput.Close(); // no terminal to answer anything
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (whileRunning is not null)
            await whileRunning(process);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw;
        }
        return (process.ExitCode, await stdout, await stderr);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
