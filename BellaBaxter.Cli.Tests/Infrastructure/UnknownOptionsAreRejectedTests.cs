using BellaBaxter.Cli.Tests.Helpers;

namespace BellaBaxter.Cli.Tests.Infrastructure;

/// <summary>
/// #1618 — the whole CLI rejects options it does not declare (strict parsing, exit 255, a dedicated message for
/// <c>--token</c>), under Security's conditions S1–S6 (<c>bb-sec/advice/2026-10-30_cli-opciones-desconocidas.md</c>).
/// </summary>
/// <remarks>
/// <para><b>Zone: critical (TDD-mandatory, human review before merging).</b> The case that matters is a credential in
/// argv: <c>bella sdk run --token &lt;scoped&gt; -- cmd</c> runs the child with the AMBIENT key, broader than the token
/// the user meant, and exits 0. cc-tdd-coach's tree puts credentials/API keys on the critical path, so every
/// rejection here has its negative half: no HTTP request, no child process.</para>
/// <para>Runs the REAL built <c>bella</c> as a child process (<see cref="BellaProcess"/>) against a loopback fake API
/// (<see cref="FakeBellaServer"/>) that records every request. In-process tests that construct the command and its
/// <c>CommandContext</c> by hand cannot see this: the bug lives in how <c>Program.cs</c> configures Spectre's parser
/// (no <c>UseStrictParsing</c>) and in the <c>--</c> stripping it does before the parser runs.</para>
/// <para>S2's third leg (the ambient key is not READ) is not observable from outside the process; the tests pin the
/// two observable legs (no request reaches the server, no child is started).</para>
/// </remarks>
public class UnknownOptionsAreRejectedTests
{
    /// <summary>A dummy ambient key (not a real credential): it lets every command get past "not logged in".</summary>
    private const string DummyAmbientKey =
        "bax-0123456789abcdef0123456789abcdef-00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";

    /// <summary>A dummy value passed after <c>--token</c> (not a real credential). S1: it must never be echoed.</summary>
    private const string DummyArgvToken = "bax-qa1618dummy0000000000000000000-feedfacefeedfacefeedfacefeedface";

    private static bool OnUnix => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    /// <summary>
    /// The prefix for <paramref name="command"/>. <c>run</c> gets explicit <c>-p</c>/<c>-e</c> (declared options) so
    /// that, if the unknown option were ignored, it would reach the server instead of stopping on missing context.
    /// <c>shell open</c> gets its declared positional context.
    /// </summary>
    private static string[] Prefix(string command) => command switch
    {
        "run" => ["run", "-p", "sandbox", "-e", "dev"],
        "shell open" => ["shell", "open", "sandbox/dev"],
        _ => command.Split(' '),
    };

    private static FakeBellaServer ServerThatAnswersRun() =>
        FakeBellaServer.WithSandboxDev()
            .On("GET", "/api/v1/projects/sandbox/environments/dev/secrets",
                """{"environmentSlug":"dev","environmentName":"Dev","secrets":{"QA_DUMMY":"1"},"version":1}""");

    private static Dictionary<string, string> AmbientEnvironment(FakeBellaServer server) => new()
    {
        ["BELLA_BAXTER_URL"] = server.Url,
        ["BELLA_BAXTER_API_KEY"] = DummyAmbientKey,
    };

    // ── Unknown option before `--` → exit 255, no HTTP, no child ──────────────────────────────────────

    public static TheoryData<string, string[]> UnknownOptionCases()
    {
        var data = new TheoryData<string, string[]>();
        foreach (var command in new[] { "run", "exec", "sdk run", "shell open" })
        {
            data.Add(command, ["--token", "x"]);   // S3: separate value
            data.Add(command, ["--token=x"]);      // S3: attached value
            data.Add(command, ["-k", "x"]);        // S3: a short alias (no command declares -k)
            data.Add(command, ["--no-such-flag"]); // the generic typo case of the issue's repro
        }
        return data;
    }

    /// <summary>#1618 AC "Unknown option before -- is rejected" + S2 (rejection first) + S3 (forms).</summary>
    [Theory]
    [Trait("Issue", "1618")]
    [Trait("Criterion", "AC-unknown-option-rejected;S2;S3-forms")]
    [Trait("Zone", "critical")]
    [MemberData(nameof(UnknownOptionCases))]
    public async Task An_unknown_option_before_the_separator_exits_255_with_no_request_and_no_child(
        string command, string[] unknown)
    {
        Assert.SkipUnless(OnUnix, "the recording child is a /bin/sh script");
        using var server = ServerThatAnswersRun();
        using var bella = new BellaProcess();

        var (exit, stdout, stderr) = await bella.RunAsync(
            AmbientEnvironment(server),
            [.. Prefix(command), .. unknown, "--", .. bella.ChildCommand]);

        Assert.False(bella.ChildStarted, $"`bella {command} {string.Join(' ', unknown)}` started the child process");
        Assert.Empty(server.Requests);
        Assert.Equal(255, exit);
        // The message names the option (S5: naming it reveals nothing that --help does not).
        var optionName = unknown[0].Split('=')[0].TrimStart('-');
        Assert.Contains(optionName, BellaProcess.Said(stdout, stderr), StringComparison.Ordinal);
    }

    // ── S3, order: an option after the child's command and before any `--` ───────────────────────────

    /// <summary>
    /// #1618 S3 (order) — <c>bella sdk run node app.js --token x</c>: only <c>--</c> separates, so <c>--token x</c> is
    /// neither discarded silently nor handed to the child. The only behaviour left is the rejection.
    /// </summary>
    [Theory]
    [Trait("Issue", "1618")]
    [Trait("Criterion", "S3-order")]
    [Trait("Zone", "critical")]
    [InlineData("sdk run")]
    [InlineData("exec")]
    public async Task An_option_after_the_childs_command_without_a_separator_is_rejected_not_swallowed_nor_passed(
        string command)
    {
        Assert.SkipUnless(OnUnix, "the recording child is a /bin/sh script");
        using var server = ServerThatAnswersRun();
        using var bella = new BellaProcess();

        var (exit, _, _) = await bella.RunAsync(
            AmbientEnvironment(server),
            [.. Prefix(command), .. bella.ChildCommand, "--token", "x"]);

        if (bella.ChildStarted)
        {
            var childArgs = bella.ChildArgs;
            Assert.Fail(childArgs.Contains("--token")
                ? $"`bella {command} <cmd> --token x` passed --token to the child without `--` (args: {string.Join(' ', childArgs)})"
                : $"`bella {command} <cmd> --token x` discarded --token silently and started the child (args: {string.Join(' ', childArgs)})");
        }
        Assert.Empty(server.Requests);
        Assert.Equal(255, exit);
    }

    // ── S1: the value after --token is never echoed ────────────────────────────────────────────────────

    /// <summary>
    /// #1618 S1 — <c>bella sdk run --token bax-&lt;dummy&gt; -- true</c>: the error names the option, never its value
    /// or the rest of argv (stderr and stdout end up in CI logs). No ambient key is set, so any <c>bax-</c> in the
    /// output can only come from argv. The exit-code assertion makes sure the guard is looking at the rejection.
    /// </summary>
    [Theory]
    [Trait("Issue", "1618")]
    [Trait("Criterion", "S1")]
    [Trait("Zone", "critical")]
    [InlineData("sdk run", false)]
    [InlineData("sdk run", true)]
    [InlineData("exec", false)]
    [InlineData("exec", true)]
    public async Task The_value_after_token_appears_in_neither_stdout_nor_stderr(string command, bool attached)
    {
        Assert.SkipUnless(OnUnix, "the child is /usr/bin/true");
        using var server = new FakeBellaServer();
        using var bella = new BellaProcess();

        string[] token = attached ? [$"--token={DummyArgvToken}"] : ["--token", DummyArgvToken];
        var (exit, stdout, stderr) = await bella.RunAsync(
            new Dictionary<string, string> { ["BELLA_BAXTER_URL"] = server.Url },
            [.. Prefix(command), .. token, "--", "true"]);

        Assert.DoesNotContain("bax-", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("bax-", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("feedface", stdout + stderr, StringComparison.Ordinal);
        Assert.Equal(255, exit);
    }

    // ── S4: the dedicated --token message ──────────────────────────────────────────────────────────────

    /// <summary>
    /// #1618 S4 — the fixed part of Security's text: «--token is not an option» and the route through
    /// <c>BELLA_BAXTER_API_KEY</c>, with the command's own name (<c>bella sdk run</c> / <c>bella exec</c>).
    /// </summary>
    [Theory]
    [Trait("Issue", "1618")]
    [Trait("Criterion", "S4")]
    [Trait("Zone", "critical")]
    [InlineData("sdk run", "bella sdk run")]
    [InlineData("exec", "bella exec")]
    public async Task Token_gets_the_dedicated_message_with_the_environment_variable_route(string command, string spelled)
    {
        Assert.SkipUnless(OnUnix, "the child is /usr/bin/true");
        using var server = new FakeBellaServer();
        using var bella = new BellaProcess();

        var (exit, stdout, stderr) = await bella.RunAsync(
            new Dictionary<string, string> { ["BELLA_BAXTER_URL"] = server.Url },
            [.. Prefix(command), "--token", "x", "--", "true"]);

        var output = BellaProcess.Said(stdout, stderr);
        Assert.Contains("--token is not an option.", output, StringComparison.Ordinal);
        Assert.Contains($"To run with a scoped token: BELLA_BAXTER_API_KEY=<token> {spelled} -- <command>.", output,
            StringComparison.Ordinal);
        Assert.Equal(255, exit);
    }

    /// <summary>#1618 S4 — the other unknown options get the generic message, not the --token one.</summary>
    [Fact]
    [Trait("Issue", "1618")]
    [Trait("Criterion", "S4-generic")]
    public async Task Other_unknown_options_get_the_generic_message()
    {
        Assert.SkipUnless(OnUnix, "the child is /usr/bin/true");
        using var server = new FakeBellaServer();
        using var bella = new BellaProcess();

        var (exit, stdout, stderr) = await bella.RunAsync(
            new Dictionary<string, string> { ["BELLA_BAXTER_URL"] = server.Url },
            ["sdk", "run", "--no-such-flag", "--", "true"]);

        Assert.Equal(255, exit);
        Assert.Contains("no-such-flag", BellaProcess.Said(stdout, stderr), StringComparison.Ordinal);
        Assert.DoesNotContain("To run with a scoped token", BellaProcess.Said(stdout, stderr), StringComparison.Ordinal);
    }

    // ── No regression: everything after `--` reaches the child unchanged ──────────────────────────────

    /// <summary>
    /// The passthrough tokens: an ordinary option, a single-dash multi-char flag (the <c>-auto-approve</c> fix,
    /// <c>Program.cs</c> separator stripping), and <c>--token x</c> itself, which AFTER <c>--</c> belongs to the child.
    /// </summary>
    private static readonly string[] Passthrough = ["--port", "3000", "-auto-approve", "--token", "x"];

    /// <summary>
    /// #1618 AC "Passthrough intact" — control, green before the fix and must stay green after it:
    /// <c>bella run|exec|sdk run -- &lt;cmd&gt; --port 3000 -auto-approve --token x</c> hands those tokens to the child
    /// unchanged and in order.
    /// </summary>
    [Theory]
    [Trait("Issue", "1618")]
    [Trait("Criterion", "AC-passthrough-intact")]
    [Trait("Zone", "critical")]
    [InlineData("run")]
    [InlineData("exec")]
    [InlineData("sdk run")]
    public async Task Everything_after_the_separator_reaches_the_child_unchanged(string command)
    {
        Assert.SkipUnless(OnUnix, "the recording child is a /bin/sh script");
        using var server = ServerThatAnswersRun();
        using var bella = new BellaProcess();

        var (exit, stdout, stderr) = await bella.RunAsync(
            AmbientEnvironment(server),
            [.. Prefix(command), "--", .. bella.ChildCommand, .. Passthrough]);

        Assert.True(bella.ChildStarted, $"the child was not started (exit {exit}).\nstdout: {stdout}\nstderr: {stderr}");
        Assert.Equal(Passthrough, bella.ChildArgs);
        Assert.Equal(0, exit);
    }

    /// <summary>
    /// #1618 AC "Passthrough intact" for <c>shell open</c> (<c>ShellOpenCommand.cs:98</c>). RED before any fix, and
    /// NOT because of strict parsing: <c>Program.cs</c> moves everything after <c>--</c> into
    /// <c>PassthroughArgsHolder</c> before Spectre runs (commit 0ddb4ca79, 2026-04-27), and <c>ShellOpenCommand</c>
    /// reads only <c>context.Remaining.Raw</c>, which is then empty — so <c>bella shell open -- node server.js</c>
    /// opens an interactive shell and never runs the command. Found by this control on 2026-10-30.
    /// </summary>
    [Fact]
    [Trait("Issue", "1618")]
    [Trait("Criterion", "AC-passthrough-intact")]
    [Trait("Zone", "critical")]
    public async Task Shell_open_hands_everything_after_the_separator_to_the_command()
    {
        Assert.SkipUnless(OnUnix, "the recording child is a /bin/sh script");
        using var server = ServerThatAnswersRun();
        using var bella = new BellaProcess();

        var (exit, stdout, stderr) = await bella.RunAsync(
            AmbientEnvironment(server),
            [.. Prefix("shell open"), "--", .. bella.ChildCommand, .. Passthrough]);

        Assert.True(bella.ChildStarted,
            $"`bella shell open sandbox/dev -- <cmd>` did not run the command (exit {exit}).\nstdout: {stdout}\nstderr: {stderr}");
        Assert.Equal(Passthrough, bella.ChildArgs);
        Assert.Equal(0, exit);
    }
}
