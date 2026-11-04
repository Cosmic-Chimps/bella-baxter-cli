using System.Diagnostics;
using BellaCli.Commands.Exec;
using BellaCli.Commands.Orgs;
using BellaCli.Commands.Run;
using BellaCli.Commands.Sdk;
using BellaCli.Commands.Secrets;
using BellaCli.Commands.Shell;
using BellaCli.Infrastructure;
using Spectre.Console.Cli;
using Spectre.Console.Cli.Testing;

namespace BellaBaxter.Cli.Tests.Commands;

/// <summary>
/// Issue #1618 — an option a command does not declare is refused: exit 255, the option named, nothing else.
///
/// <para>Every command accepted options it did not declare, dropped them and ran with exit 0. The case that
/// mattered: <c>bella sdk run --token &lt;scoped&gt; -- cmd</c> ran the child with the AMBIENT credential and said
/// nothing (the residual of #833). Security's conditions are the test names: S1 no value echoed, S2 refused
/// before any credential, request or child, S3 every spelling and the no-<c>--</c> order, S4 the exact
/// <c>--token</c> text.</para>
///
/// <para>Two halves. In-process, <c>CommandAppTester</c> runs the real command types through the real parser
/// with the shipped <see cref="CliParsing"/> policy, on a registrar that records whether Spectre ever asked for a
/// resolver — a refusal that happens at parse time never does, which is what "before the command exists" means.
/// Out of process, the real built <c>bella</c> runs with no terminal, an isolated HOME and a server nobody
/// listens on, because the passthrough strip and the stderr/stdout split only exist in <c>Program.cs</c>.</para>
/// </summary>
public class UnknownOptionsAreRejectedTests : IDisposable
{
    private const string DummyToken = "bax-dummy-000000";

    // ── In-process: the parser with the shipped policy ───────────────────────────────────────────────

    /// <summary>Accepts Spectre's registrations and records whether a resolver was ever built.</summary>
    private sealed class NothingIsResolvedRegistrar : ITypeRegistrar
    {
        public bool ResolverBuilt { get; private set; }

        public ITypeResolver Build()
        {
            ResolverBuilt = true;
            return new RefusingResolver();
        }

        public void Register(Type service, Type implementation) { }

        public void RegisterInstance(Type service, object implementation) { }

        public void RegisterLazy(Type service, Func<object> factory) { }

        private sealed class RefusingResolver : ITypeResolver
        {
            public object? Resolve(Type? type) =>
                throw new InvalidOperationException($"'{type?.Name}' was resolved: the refusal must come before the command exists.");
        }
    }

    private static (CommandAppTester App, NothingIsResolvedRegistrar Registrar, StringWriter Stderr) Build(string[] args)
    {
        var registrar = new NothingIsResolvedRegistrar();
        var stderr = new StringWriter();
        var app = new CommandAppTester(registrar);
        app.Configure(c =>
        {
            c.SetApplicationName("bella");
            // The policy Program.cs ships, not a copy of it.
            CliParsing.Apply(c, app.Console, stderr, () => args);
            c.AddCommand<RunCommand>("run");
            c.AddCommand<DeprecatedExecCommand>("exec");
            c.AddBranch("sdk", sdk => sdk.AddCommand<SdkRunCommand>("run"));
            c.AddBranch("shell", sh => sh.AddCommand<ShellOpenCommand>("open"));
            c.AddBranch("secrets", s => s.AddCommand<GetSecretsCommand>("get"));
            c.AddBranch("org", o => o.AddCommand<OrgCurrentCommand>("current"));
        });
        return (app, registrar, stderr);
    }

    public static TheoryData<string[]> EveryCommandWithAnUnknownOption => new()
    {
        new[] { "run", "--no-such-flag", "-p", "sandbox", "-e", "dev", "--", "true" },
        new[] { "sdk", "run", "--no-such-flag", "--", "true" },
        new[] { "exec", "--no-such-flag", "--", "true" },
        new[] { "shell", "open", "--no-such-flag" },
        new[] { "secrets", "get", "--no-such-flag" },
        new[] { "org", "current", "--no-such-flag" },
    };

    [Theory]
    [MemberData(nameof(EveryCommandWithAnUnknownOption))]
    public void Uniformity_every_command_refuses_the_same_way_before_anything_is_resolved(string[] args)
    {
        var (app, registrar, stderr) = Build(args);

        var result = app.Run(args);

        Assert.Equal(-1, result.ExitCode); // 255 to a shell — Spectre's own code for a parse error
        Assert.Equal(string.Empty, result.Output); // nothing on stdout
        Assert.StartsWith("Unknown option '--no-such-flag'.", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("--help", stderr.ToString(), StringComparison.Ordinal);
        // S2 — refused at parse time: no command, no CredentialStore, no client was ever constructed.
        Assert.False(registrar.ResolverBuilt, "the parser accepted the option and Spectre went on to build the command");
    }

    public static TheoryData<string[], string> EveryTokenSpelling => new()
    {
        { new[] { "sdk", "run", "--token", DummyToken, "--", "true" }, "sdk run" },
        { new[] { "sdk", "run", $"--token={DummyToken}", "--", "true" }, "sdk run" },
        { new[] { "exec", "--token", DummyToken, "--", "true" }, "exec" },
        { new[] { "exec", $"--token={DummyToken}", "--", "true" }, "exec" },
        // S3, order — the option after the child command and no '--': only '--' separates.
        { new[] { "sdk", "run", "node", "app.js", "--token", DummyToken }, "sdk run" },
    };

    [Theory]
    [MemberData(nameof(EveryTokenSpelling))]
    public void S3_S4_every_spelling_of_token_gets_the_exact_message_and_never_its_value(string[] args, string command)
    {
        var (app, registrar, stderr) = Build(args);

        var result = app.Run(args);

        Assert.Equal(-1, result.ExitCode);
        Assert.Equal(UnknownOptionRefusal.TokenMessage(command), stderr.ToString().TrimEnd());
        // S1 — the value after --token is a token; neither stream repeats it, or anything else from argv.
        Assert.DoesNotContain("bax-", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("bax-", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("node", stderr.ToString(), StringComparison.Ordinal);
        Assert.False(registrar.ResolverBuilt);
    }

    [Fact]
    public void S3_a_short_alias_is_refused_too_and_the_value_is_still_not_echoed()
    {
        var args = new[] { "sdk", "run", "-T", DummyToken, "--", "true" };
        var (app, _, stderr) = Build(args);

        var result = app.Run(args);

        Assert.Equal(-1, result.ExitCode);
        Assert.StartsWith("Unknown option '-T'.", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("bax-", stderr.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sdk", "run")]
    [InlineData("secrets", "get")]
    public void S1_the_value_after_an_equals_sign_is_never_echoed_for_any_unknown_option(params string[] command)
    {
        // Product's condition on the generic message: `--opt=value` names the option and nothing after the '='.
        var args = command.Concat([$"--foo={DummyToken}"]).ToArray();
        var (app, _, stderr) = Build(args);

        var result = app.Run(args);

        Assert.Equal(-1, result.ExitCode);
        Assert.StartsWith("Unknown option '--foo'.", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("bax-", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("bax-", result.Output, StringComparison.Ordinal);
    }

    public static TheoryData<string[]> AnyUnknownOptionWithASecretValue => new()
    {
        new[] { "run", "--secret-key", DummyToken, "-p", "sandbox", "-e", "dev", "--", "true" },
        new[] { "exec", "--secret-key", DummyToken, "--", "true" },
        new[] { "sdk", "run", "--secret-key", DummyToken, "--", "true" },
        new[] { "shell", "open", "--secret-key", DummyToken },
        new[] { "run", $"--secret-key={DummyToken}", "--", "true" },
    };

    [Theory]
    [MemberData(nameof(AnyUnknownOptionWithASecretValue))]
    public void S1_the_value_of_any_unknown_option_is_never_echoed_not_only_tokens(string[] args)
    {
        // Security's condition on the PR: the rule is for every undeclared option on every child-running
        // command, because what follows an option nobody declared is as likely to be a secret as --token's value.
        var (app, _, stderr) = Build(args);

        var result = app.Run(args);

        Assert.Equal(-1, result.ExitCode);
        Assert.StartsWith("Unknown option '--secret-key'.", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("bax-", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("bax-", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void S4_token_on_a_command_that_is_not_sdk_run_or_exec_gets_the_generic_message()
    {
        // `run` injects secrets itself and has no scoped-token path to point at.
        var args = new[] { "run", "--token", DummyToken, "--", "true" };
        var (app, _, stderr) = Build(args);

        app.Run(args);

        Assert.StartsWith("Unknown option '--token'.", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("bax-", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_message_tells_a_child_running_command_where_the_childs_arguments_go()
    {
        var args = new[] { "run", "--port", "3000", "--", "true" };
        var (app, _, stderr) = Build(args);

        app.Run(args);

        Assert.Contains("go after '--'", stderr.ToString(), StringComparison.Ordinal);

        var other = new[] { "org", "current", "--port", "3000" };
        var (app2, _, stderr2) = Build(other);
        app2.Run(other);
        Assert.DoesNotContain("go after '--'", stderr2.ToString(), StringComparison.Ordinal);
    }

    public static TheoryData<string[]> ChildArgumentsAfterTheSeparator => new()
    {
        new[] { "run", "-p", "sandbox", "-e", "dev", "--", "node", "app.js", "--port", "3000" },
        new[] { "sdk", "run", "--", "node", "app.js", "--port", "3000" },
        new[] { "exec", "--", "node", "app.js", "--port", "3000" },
        new[] { "shell", "open", "p/e", "--", "node", "app.js", "--port", "3000" },
    };

    [Theory]
    [MemberData(nameof(ChildArgumentsAfterTheSeparator))]
    public void Strict_parsing_never_looks_past_the_separator(string[] args)
    {
        // Spectre's parser sees '--' here (Program.cs strips what follows before it runs; the out-of-process
        // tests below cover that path). Either way an undeclared option AFTER '--' is not an error: the parse
        // succeeds and Spectre goes on to build the command — which this registrar refuses, so the run ends
        // in a resolution error, never in an unknown-option refusal.
        var (app, registrar, stderr) = Build(args);

        app.Run(args);

        Assert.True(registrar.ResolverBuilt, "the parser refused something after '--'");
        Assert.DoesNotContain("Unknown option", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Other_parse_errors_write_only_the_message_on_stderr_never_the_command_line()
    {
        // Security B2: Spectre's block for any parse error repeats argv. An unknown command is still refused with
        // Spectre's exit code, but only its one-line message is written, and on stderr.
        var args = new[] { "nonsense" };
        var (app, _, stderr) = Build(args);

        var result = app.Run(args);

        Assert.Equal(-1, result.ExitCode);
        Assert.Equal(string.Empty, result.Output);
        Assert.Contains("Unknown command 'nonsense'", stderr.ToString(), StringComparison.Ordinal);
        Assert.Single(stderr.ToString().TrimEnd().Split('\n'));
    }

    public static TheoryData<string[]> ParseErrorsNextToASecretValue => new()
    {
        // B1 — an apostrophe in the option name: Spectre's message no longer matches a [^']+ name.
        new[] { "sdk", "run", "--a'b", DummyToken, "--", "true" },
        // B2 — a typo in the command, and an option missing its value, both before a token.
        new[] { "sdkk", "run", "--token", DummyToken, "--", "true" },
        new[] { "run", "-p", "--token", DummyToken, "--", "true" },
    };

    [Theory]
    [MemberData(nameof(ParseErrorsNextToASecretValue))]
    public void S1_B1_B2_no_parse_error_echoes_a_value_from_the_command_line(string[] args)
    {
        var (app, registrar, stderr) = Build(args);

        var result = app.Run(args);

        Assert.Equal(-1, result.ExitCode);
        Assert.Equal(string.Empty, result.Output);
        Assert.NotEqual(string.Empty, stderr.ToString());
        Assert.DoesNotContain("bax-", stderr.ToString(), StringComparison.Ordinal);
        Assert.False(registrar.ResolverBuilt);
    }

    [Theory]
    [InlineData("sdk", "run")]
    [InlineData("exec")]
    public void S4_I1_the_token_message_is_the_text_Security_signed_character_for_character(params string[] command)
    {
        // Compared with the literal, not with TokenMessage(): a change to the code must fail here.
        var signed = command.Length == 2
            ? "--token is not an option. To run with a scoped token: BELLA_BAXTER_API_KEY=<token> bella sdk run -- <command>. If you passed a real token, it may now be in your shell history or CI log; revoke it if that log is shared."
            : "--token is not an option. To run with a scoped token: BELLA_BAXTER_API_KEY=<token> bella exec -- <command>. If you passed a real token, it may now be in your shell history or CI log; revoke it if that log is shared.";
        var args = command.Concat(["--token", DummyToken, "--", "true"]).ToArray();
        var (app, _, stderr) = Build(args);

        app.Run(args);

        Assert.Equal(signed, stderr.ToString().TrimEnd());
    }

    [Fact]
    public void M3_control_characters_in_an_option_name_are_shown_escaped()
    {
        var args = new[] { "sdk", "run", "--a\u001b[31mb", "--", "true" };
        var (app, _, stderr) = Build(args);

        var result = app.Run(args);

        // Spectre 0.55 refuses this name itself ("Invalid long option name."), so the raw escape never reaches the
        // terminal either way; Printable is the guard for a parser that lets it through.
        Assert.Equal(-1, result.ExitCode);
        Assert.DoesNotContain("\u001b", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", result.Output, StringComparison.Ordinal);
        Assert.Equal("a\\u001B[31mb", UnknownOptionRefusal.Printable("a\u001b[31mb"));
        Assert.Equal("plain-name", UnknownOptionRefusal.Printable("plain-name"));
    }

    // ── Out of process: the real bella, with Program.cs's passthrough strip and stream split ─────────

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bella-opts-" + Guid.NewGuid().ToString("N"));
    private readonly string _home;
    private readonly string _work;

    public UnknownOptionsAreRejectedTests()
    {
        _home = Path.Combine(_root, "home");
        _work = Path.Combine(_root, "work");
        Directory.CreateDirectory(_home);
        Directory.CreateDirectory(_work);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private async Task<(int Exit, string Stdout, string Stderr)> RunBellaAsync(params string[] args)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "bella.dll");
        Assert.True(File.Exists(dll), $"{dll} is not next to the tests; the CLI project reference should copy it.");

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _work,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(dll);
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.Environment["HOME"] = _home;
        psi.Environment["USERPROFILE"] = _home;
        // An ambient key, so a command that got past the parser WOULD have a credential to run with (S2), and a
        // server nobody listens on, so a request that got sent would fail loudly rather than silently.
        psi.Environment["BELLA_BAXTER_API_KEY"] = "bax-ambient-000000";
        psi.Environment["BELLA_BAXTER_URL"] = "http://127.0.0.1:9";
        foreach (var v in new[] { "BAXTER_URL", "BELLA_API_KEY", "BELLA_BAXTER_ACCESS_TOKEN" })
            psi.Environment.Remove(v);

        using var process = Process.Start(psi)!;
        process.StandardInput.Close(); // no terminal to answer anything
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await stdout, await stderr);
    }

    [Theory]
    [InlineData("sdk", "run")]
    [InlineData("exec")]
    public async Task S1_S2_the_real_bella_refuses_token_on_stderr_with_exit_255_and_starts_nothing(params string[] command)
    {
        var marker = Path.Combine(_work, "child-ran");
        var args = command.Concat(["--token", DummyToken, "--", "/bin/sh", "-c", $"touch '{marker}'"]).ToArray();

        var (exit, stdout, stderr) = await RunBellaAsync(args);

        Assert.Equal(255, exit);
        Assert.Equal(UnknownOptionRefusal.TokenMessage(string.Join(' ', command)), stderr.TrimEnd());
        Assert.Equal(string.Empty, stdout);
        Assert.DoesNotContain("bax-", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("bax-", stdout, StringComparison.Ordinal);
        Assert.False(File.Exists(marker), "the child process was started");
    }

    [Fact]
    public async Task The_real_bella_refuses_an_unknown_option_on_run_before_fetching_anything()
    {
        var (exit, stdout, stderr) = await RunBellaAsync("run", "--no-such-flag", "-p", "sandbox", "-e", "dev", "--", "true");

        Assert.Equal(255, exit);
        Assert.StartsWith("Unknown option '--no-such-flag'.", stderr, StringComparison.Ordinal);
        Assert.Equal(string.Empty, stdout);
        // Nothing reached the server at 127.0.0.1:9 — a connection attempt would have been reported.
        Assert.DoesNotContain("127.0.0.1", stderr + stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sdk", "run")]
    [InlineData("exec")]
    [InlineData("shell", "open", "p/e")]
    public async Task Passthrough_intact_everything_after_the_separator_reaches_the_child(params string[] command)
    {
        // The reference case of #1618: `bella run -- node app.js --port 3000` still passes `--port 3000`.
        // (`run` itself needs a server to fetch from, so it is covered in-process above.)
        var args = command.Concat(["--", "/bin/echo", "app.js", "--port", "3000", "-auto-approve"]).ToArray();

        var (exit, stdout, _) = await RunBellaAsync(args);

        Assert.Equal(0, exit);
        Assert.Contains("app.js --port 3000 -auto-approve", stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sdk", "run")]
    [InlineData("exec")]
    [InlineData("shell", "open", "p/e")]
    public async Task Passthrough_intact_a_second_separator_belongs_to_the_child(params string[] command)
    {
        // Security m1: `bella shell open p/e -- npm run x -- --watch` must hand npm its own '--'.
        var args = command.Concat(["--", "/bin/echo", "run", "x", "--", "--watch"]).ToArray();

        var (exit, stdout, _) = await RunBellaAsync(args);

        Assert.Equal(0, exit);
        Assert.Contains("run x -- --watch", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task B2_the_real_bella_writes_a_mistyped_command_to_stderr_without_the_token()
    {
        var (exit, stdout, stderr) = await RunBellaAsync("sdkk", "run", "--token", DummyToken, "--", "true");

        Assert.Equal(255, exit);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("sdkk", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("bax-", stderr + stdout, StringComparison.Ordinal);
    }
}
