using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Infrastructure;

/// <summary>
/// What the CLI does with an exception that escapes a command (#1313).
///
/// <para>Spectre.Console.Cli's <c>CommandApp.RunAsync</c> catches every exception itself, prints
/// <c>Error: …</c> on STDOUT and returns -1 (255 to a shell), so a <c>catch</c> around
/// <c>app.RunAsync</c> in <c>Program.cs</c> is never reached. The documented refusal of an untrusted
/// <c>.bella</c> server — stderr, exit 1, nothing sent — therefore came out as stdout and 255.</para>
///
/// <para>Installing ANY handler replaces Spectre's rendering, so everything that is not that refusal is
/// rendered the way Spectre does it (parse errors keep their usage snippet and exit code), instead of
/// switching the whole CLI to <c>PropagateExceptions</c>, which would turn every command error into a crash.</para>
/// </summary>
public static class CliExceptionHandler
{
    /// <summary>Returns the process exit code for <paramref name="ex"/>.</summary>
    public static int Handle(Exception ex, IAnsiConsole console, TextWriter stderr, int cancellationExitCode)
    {
        // Advisory clients-1 follow-up — an untrusted .bella server is a refusal with a fix, not a crash.
        if (OriginTrustPrompt.Find(ex) is { } untrusted)
        {
            stderr.WriteLine(untrusted.Message);
            return 1;
        }

        // Spectre's own behaviour, unchanged (CommandApp.RunAsync in Spectre.Console.Cli 0.55).
        if (ex is OperationCanceledException)
            return cancellationExitCode;

        if (ex is CommandAppException { Pretty: not null } pretty)
        {
            console.Write(pretty.Pretty);
        }
        else
        {
            console.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            if (ex.InnerException is CommandAppException { Pretty: not null } inner)
                console.Write(inner.Pretty);
        }

        return -1;
    }
}
