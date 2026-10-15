using System.Diagnostics;

namespace BellaBaxter.Cli.Tests.Infrastructure;

/// <summary>
/// #1313 — a refusal to follow an untrusted <c>.bella</c> server is documented as stderr, exit 1, nothing sent
/// (<c>docs/agents/cli.md</c>, README). It came out on STDOUT with exit 255: Spectre.Console.Cli's
/// <c>CommandApp.RunAsync</c> catches the exception first, prints <c>Error: …</c> and returns -1, so the
/// <c>catch</c> in <c>Program.cs</c> never ran.
/// </summary>
/// <remarks>
/// Runs the REAL built <c>bella</c> as a child process — no terminal (stdin and stdout are pipes), an isolated
/// <c>HOME</c> (so the real <c>config.json</c> and credentials are never read or written), and a working
/// directory whose <c>.bella</c> names a server nobody trusts. The in-process tests inject the confirmer and
/// call the services directly, which is how this escaped.
/// </remarks>
public class UntrustedServerExitContractTests : IDisposable
{
    private const string Untrusted = "http://127.0.0.1:9";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bella-exit-" + Guid.NewGuid().ToString("N"));
    private readonly string _home;
    private readonly string _repo;

    public UntrustedServerExitContractTests()
    {
        _home = Path.Combine(_root, "home");
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_home);
        Directory.CreateDirectory(_repo);
        File.WriteAllText(Path.Combine(_repo, ".bella"), $"project = \"p\"\nurl = \"{Untrusted}\"\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private Task<(int Exit, string Stdout, string Stderr)> RunAsync(params string[] args) =>
        RunWithEnvironmentAsync(new Dictionary<string, string>(), args);

    private async Task<(int Exit, string Stdout, string Stderr)> RunWithEnvironmentAsync(
        Dictionary<string, string> environment, params string[] args)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "bella.dll");
        Assert.True(File.Exists(dll), $"{dll} is not next to the tests; the CLI project reference should copy it.");

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _repo,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(dll);
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.Environment["HOME"] = _home;
        psi.Environment["USERPROFILE"] = _home;
        foreach (var v in new[] { "BELLA_BAXTER_URL", "BAXTER_URL", "BELLA_BAXTER_API_KEY", "BELLA_API_KEY", "BELLA_BAXTER_ACCESS_TOKEN" })
            psi.Environment.Remove(v);
        foreach (var (name, value) in environment)
            psi.Environment[name] = value;

        using var process = Process.Start(psi)!;
        process.StandardInput.Close(); // no terminal to answer anything
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await stdout, await stderr);
    }

    [Fact]
    public async Task An_untrusted_bella_server_is_refused_on_stderr_with_exit_1_and_stdout_stays_empty()
    {
        // `login --api-key` reads the address in the command body, with nothing around it that handles the refusal.
        var (exit, stdout, stderr) = await RunAsync("login", "--api-key", "bax-aaaa-bbbb");

        Assert.Equal(1, exit);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains(Untrusted, stderr, StringComparison.Ordinal);
        Assert.Contains("bella config trust", stderr, StringComparison.Ordinal);
        Assert.Contains("nothing was sent", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Error:", stderr, StringComparison.Ordinal);
        // and nothing was stored for that server
        Assert.False(File.Exists(Path.Combine(_home, ".config", "bella-cli", "apikey.json")));
    }

    [Fact]
    public async Task The_refusal_is_the_same_for_a_command_that_reads_the_server_through_a_client()
    {
        // A key from the environment, so `secrets list` reaches CreateClient → the trust check, uncaught.
        var (exit, stdout, stderr) = await RunWithEnvironmentAsync(
            new() { ["BELLA_BAXTER_API_KEY"] = "bax-aaaa-bbbb" }, "secrets", "list");

        Assert.Equal(1, exit);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains(Untrusted, stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Other_errors_keep_Spectres_rendering_and_exit_code()
    {
        // The handler that maps the refusal must not change anything else: a parse error is still Spectre's
        // own "Error:" block with its usage snippet on stdout, exit 255 (-1).
        var (exit, stdout, stderr) = await RunAsync("nonsense");

        Assert.Equal(255, exit);
        Assert.Contains("Error: Unknown command 'nonsense'.", stdout, StringComparison.Ordinal);
        Assert.Contains("No such command", stdout, StringComparison.Ordinal);
        Assert.Equal(string.Empty, stderr);
    }
}
