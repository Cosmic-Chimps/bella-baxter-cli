using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Infrastructure;

/// <summary>
/// How <c>bella</c> parses its command line: strictly, with <see cref="CliExceptionHandler"/> installed (#1618).
///
/// <para>One place, so that <c>Program.cs</c> and a test built on <c>CommandAppTester</c> configure the SAME
/// policy: an option a command does not declare is a parse error (exit 255, the code Spectre already uses for
/// parse errors), not something to drop. Everything after <c>--</c> is stripped before Spectre sees it
/// (<c>Program.cs</c>, <c>PassthroughArgsHolder</c>), so strict parsing never touches the child's arguments.</para>
/// </summary>
public static class CliParsing
{
    /// <param name="arguments">
    /// The arguments Spectre is about to parse, read when an error is rendered (they tell the refusal which
    /// command was being run; they are never echoed).
    /// </param>
    public static void Apply(
        IConfigurator config,
        IAnsiConsole console,
        TextWriter stderr,
        Func<IReadOnlyList<string>> arguments)
    {
        config.UseStrictParsing();
        // #1313 — Spectre catches every exception inside RunAsync and prints it on stdout with exit 255, so the
        // untrusted-.bella refusal (stderr, exit 1) and the unknown-option refusal (#1618) have to be mapped HERE;
        // everything else is rendered as before.
        config.SetExceptionHandler((ex, _) =>
            CliExceptionHandler.Handle(ex, console, stderr, config.Settings.CancellationExitCode, arguments()));
    }
}
