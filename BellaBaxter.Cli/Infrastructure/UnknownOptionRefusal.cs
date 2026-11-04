using System.Text.RegularExpressions;
using Spectre.Console.Cli;

namespace BellaCli.Infrastructure;

/// <summary>
/// What the CLI says when a command is given an option it does not declare (#1618).
///
/// <para>Until #1618 the whole app parsed in Spectre's relaxed mode: an unknown option was dropped and the
/// command ran, exit 0. The case that mattered was <c>bella sdk run --token &lt;scoped&gt; -- cmd</c>, which
/// ran the child with the AMBIENT credential — broader than the token the operator meant — and said
/// nothing. Parsing is strict now (<see cref="CliParsing"/>), so the option is refused before any
/// credential is read, any request is sent or any child is started.</para>
///
/// <para>Spectre's own rendering of that refusal is not used, on purpose: its "pretty" message repeats the
/// whole command line under the error, and the value after <c>--token</c> is a token. The text here names the
/// option and never its value or the rest of argv (Security S1), and goes to stderr.</para>
/// </summary>
public static partial class UnknownOptionRefusal
{
    // Greedy, so a name that itself holds an apostrophe (`--a'b`) still matches (Security B1). If Spectre's text ever
    // changes, Describe returns null and the caller still writes only the parse error's message (B2), never argv.
    [GeneratedRegex(@"^Unknown option '(?<name>.+)'\.$", RegexOptions.Singleline)]
    private static partial Regex UnknownOptionMessage();

    /// <summary>
    /// The refusal for <paramref name="ex"/>, or <c>null</c> when it is not Spectre's unknown-option parse error.
    /// <paramref name="arguments"/> are the arguments Spectre parsed (everything after <c>--</c> already
    /// stripped); they are only used to tell which command was being run, never echoed.
    /// </summary>
    public static string? Describe(Exception ex, IReadOnlyList<string> arguments)
    {
        if (ex is not CommandParseException)
            return null;

        var match = UnknownOptionMessage().Match(ex.Message);
        if (!match.Success)
            return null;

        var name = match.Groups["name"].Value;
        var shown = Printable(name);
        var command = CommandPath(arguments);

        // Security S4 — the credential-widening case gets the one message that says where the token goes.
        if (name == "token" && command is "sdk run" or "exec")
            return TokenMessage(command);

        return GenericMessage(shown.Length == 1 ? "-" + shown : "--" + shown, startsAChild: command is not null);
    }

    /// <summary>
    /// Every other parse error (an unknown command, an option without its value…): Spectre's message alone, on one
    /// line. Spectre's "pretty" block repeats the command line under it, and a typo in a subcommand
    /// (<c>bella sdkk run --token …</c>) is as likely as an unknown option, so that block is never written (B2).
    /// </summary>
    public static string ParseErrorMessage(CommandParseException ex) => Printable(ex.Message);

    /// <summary>Control characters from argv are shown escaped, so they cannot move the cursor or recolour stderr (m3).</summary>
    internal static string Printable(string text)
    {
        if (!text.Any(char.IsControl))
            return text;

        var sb = new System.Text.StringBuilder(text.Length + 8);
        foreach (var c in text)
            sb.Append(char.IsControl(c) ? $"\\u{(int)c:X4}" : c.ToString());
        return sb.ToString();
    }

    /// <summary>The exact text Security signed for <c>--token</c> on <c>sdk run</c> and <c>exec</c> (S4).</summary>
    public static string TokenMessage(string command) =>
        $"--token is not an option. To run with a scoped token: BELLA_BAXTER_API_KEY=<token> bella {command} -- <command>. "
        + "If you passed a real token, it may now be in your shell history or CI log; revoke it if that log is shared.";

    /// <summary>Every other unknown option: the option, and where the options it meant live.</summary>
    public static string GenericMessage(string option, bool startsAChild) =>
        $"Unknown option '{option}'. Run the command with --help to see the options it accepts."
        + (startsAChild ? " Arguments for the program it starts go after '--'." : string.Empty);

    /// <summary>
    /// The command that starts a child process, when that is what was being run: <c>run</c>, <c>sdk run</c>,
    /// <c>exec</c> or <c>shell open</c>; <c>null</c> for every other command.
    /// </summary>
    internal static string? CommandPath(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
            return null;

        return arguments[0] switch
        {
            "run" => "run",
            "exec" => "exec",
            "sdk" when arguments.Count > 1 && arguments[1] == "run" => "sdk run",
            "shell" when arguments.Count > 1 && arguments[1] == "open" => "shell open",
            _ => null,
        };
    }
}
