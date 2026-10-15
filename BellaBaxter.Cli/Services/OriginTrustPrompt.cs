using Spectre.Console;

namespace BellaCli.Services;

/// <summary>
/// Advisory clients-1 follow-up (cli-bella-url), security team 2026-10-10 — the one question the CLI asks
/// before it follows a server a working-directory <c>.bella</c> names and the user has not trusted.
/// </summary>
public static class OriginTrustPrompt
{
    /// <summary>
    /// The <see cref="ConfigService.OriginConfirmer"/> the CLI installs: asks only when <paramref name="canAsk"/>
    /// says there is an operator to answer (a terminal, not <c>--json</c>, not CI), otherwise null — "cannot
    /// ask", which the caller turns into a refusal. A seam so a test can exercise the REAL question.
    /// </summary>
    public static Func<UntrustedBellaServer, bool?> CreateConfirmer(Func<bool> canAsk) =>
        server => canAsk() ? Ask(server) : null;

    /// <summary>
    /// Shows the origin, the file it came from and the server the user has configured, and asks.
    /// Default No: Enter must never trust a stranger's server.
    /// </summary>
    public static bool Ask(UntrustedBellaServer server)
    {
        AnsiConsole.MarkupLine("[yellow]⚠ This directory's .bella names a Bella server you have not used before.[/]");
        AnsiConsole.MarkupLine($"  [white]Server:[/]     [cyan]{Markup.Escape(server.Origin)}[/]");
        AnsiConsole.MarkupLine($"  [white]From:[/]       [dim]{Markup.Escape(server.BellaFile)}[/]");
        AnsiConsole.MarkupLine($"  [white]Configured:[/] [dim]{Markup.Escape(server.ConfiguredServer)}[/]");
        AnsiConsole.MarkupLine(
            "[dim]A .bella comes with any repository you clone. A look-alike server can show you a login page that"
            + " steals your password; only continue if you recognise this address.[/]");
        return AnsiConsole.Confirm($"Trust {Markup.Escape(server.Origin)} and remember it?", defaultValue: false);
    }

    /// <summary>The refusal in <paramref name="ex"/> or anything it wraps, or null.</summary>
    public static UntrustedBellaServerException? Find(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is UntrustedBellaServerException found)
                return found;
        return null;
    }
}
