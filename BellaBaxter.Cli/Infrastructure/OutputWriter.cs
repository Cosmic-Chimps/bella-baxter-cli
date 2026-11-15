using System.Text.Json;
using Spectre.Console;

namespace BellaCli.Infrastructure;

public interface IOutputWriter
{
    /// <summary>
    /// Runs <paramref name="work"/>, showing progress only where progress belongs.
    /// </summary>
    /// <remarks>
    /// <para>Issue #742 — this exists because progress was written with <c>AnsiConsole.Status()</c>
    /// directly, which bypasses this writer entirely and lands on STDOUT. In JSON mode stdout then
    /// carried the progress line AND the document, so <c>bella secrets get --json | jq .</c> failed
    /// while stderr sat empty. The pilot's Ansible role works around it by parsing only the last
    /// line.</para>
    ///
    /// <para>The rule belongs here rather than in each command because there were 35 call sites and
    /// the next one added would reintroduce it. A command says WHAT it is doing; the writer decides
    /// whether saying so is allowed on this stream.</para>
    /// </remarks>
    Task StatusAsync(string message, Func<Task> work);

    /// <summary>
    /// As above, for work that reports a CHANGE of phase — "Opening browser…" becoming
    /// "Waiting for browser login…". The callback receives an updater; in JSON mode it does nothing,
    /// so the command does not have to know which mode it is in.
    /// </summary>
    Task StatusAsync(string message, Func<Action<string>, Task> work);

    void WriteObject<T>(T obj);
    void WriteList<T>(IEnumerable<T> items);
    void WriteTable(string[] headers, IEnumerable<string[]> rows);
    void WriteSuccess(string message);
    void WriteError(string message, string? code = null);
    void WriteWarning(string message);
    void WriteInfo(string message);
}

public class HumanOutputWriter(IAnsiConsole? errorConsole = null) : IOutputWriter
{
    /// <summary>
    /// The console errors are written to: STDERR, as JSON mode already does (spec 108, R15). An error on stdout is read as
    /// output by anything that pipes the command; on stderr it stays where a script and a person both look for a failure.
    /// Built per call from <see cref="Console.Error"/>, so a redirected stream is honoured, unless a test supplies one.
    /// </summary>
    private IAnsiConsole ErrorConsole()
    {
        if (errorConsole is not null)
            return errorConsole;

        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });
        // A redirected stream has no width to measure, and Spectre then wraps at 80: a link split across two lines is not a link.
        if (Console.IsErrorRedirected)
            console.Profile.Width = 4096;
        return console;
    }

    /// <summary>Shows the spinner, exactly as before.</summary>
    public async Task StatusAsync(string message, Func<Task> work) =>
        await AnsiConsole.Status().StartAsync(message, async _ => await work());

    /// <inheritdoc />
    public async Task StatusAsync(string message, Func<Action<string>, Task> work) =>
        await AnsiConsole
            .Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync(message, async ctx => await work(next => ctx.Status(next)));

    public void WriteObject<T>(T obj)
    {
        var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
        AnsiConsole.WriteLine(json);
    }

    public void WriteList<T>(IEnumerable<T> items)
    {
        foreach (var item in items)
            WriteObject(item);
    }

    public void WriteTable(string[] headers, IEnumerable<string[]> rows)
    {
        var table = new Table();
        table.Border(TableBorder.Minimal);
        foreach (var h in headers)
            table.AddColumn(new TableColumn($"[bold]{Markup.Escape(h)}[/]"));

        foreach (var row in rows)
            table.AddRow(row.Select(Markup.Escape).ToArray());

        AnsiConsole.Write(table);
    }

    public void WriteSuccess(string message) =>
        AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(message)}");

    public void WriteError(string message, string? code = null)
    {
        var suffix = code is not null ? $" [grey]({Markup.Escape(code)})[/]" : string.Empty;
        ErrorConsole().MarkupLine($"[red]✗[/] {Markup.Escape(message)}{suffix}");
    }

    public void WriteWarning(string message) =>
        AnsiConsole.MarkupLine($"[yellow]⚠[/] {Markup.Escape(message)}");

    public void WriteInfo(string message) =>
        AnsiConsole.MarkupLine($"[blue]ℹ[/] {Markup.Escape(message)}");
}

public class JsonOutputWriter : IOutputWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Runs the work and says nothing. Stdout is the document's, and nothing else may share it.
    /// </summary>
    /// <remarks>
    /// Progress is not redirected to stderr here, it is DROPPED. A spinner is a terminal animation —
    /// redrawn with control characters for a human watching — and a machine reading stderr gains
    /// nothing from it. Errors and warnings already go to stderr (below), which is where a scripted
    /// caller looks when something is wrong.
    /// </remarks>
    public Task StatusAsync(string message, Func<Task> work) => work();

    /// <inheritdoc />
    /// <remarks>The updater is a no-op: there is no line to rewrite, because nothing was printed.</remarks>
    public Task StatusAsync(string message, Func<Action<string>, Task> work) => work(_ => { });

    public void WriteObject<T>(T obj) =>
        Console.WriteLine(JsonSerializer.Serialize(obj, Options));

    public void WriteList<T>(IEnumerable<T> items) =>
        Console.WriteLine(JsonSerializer.Serialize(items, Options));

    public void WriteTable(string[] headers, IEnumerable<string[]> rows)
    {
        var list = rows.Select(r => headers.Zip(r).ToDictionary(x => x.First, x => x.Second));
        Console.WriteLine(JsonSerializer.Serialize(list, Options));
    }

    public void WriteSuccess(string message) =>
        Console.WriteLine(JsonSerializer.Serialize(new { success = true, message }, Options));

    public void WriteError(string message, string? code = null) =>
        Console.Error.WriteLine(JsonSerializer.Serialize(new { error = message, code }, Options));

    public void WriteWarning(string message) =>
        Console.Error.WriteLine(JsonSerializer.Serialize(new { warning = message }, Options));

    public void WriteInfo(string message) { /* suppress in JSON mode */ }
}
