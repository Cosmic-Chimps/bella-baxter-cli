using System.Text.Json;
using Spectre.Console;

namespace BellaCli.Infrastructure;

public interface IOutputWriter
{
    void WriteObject<T>(T obj);
    void WriteList<T>(IEnumerable<T> items);
    void WriteTable(string[] headers, IEnumerable<string[]> rows);
    void WriteSuccess(string message);
    void WriteError(string message, string? code = null);
    void WriteWarning(string message);
    void WriteInfo(string message);
}

public class HumanOutputWriter : IOutputWriter
{
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
        table.Border(TableBorder.Rounded);
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
        AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(message)}{suffix}");
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
