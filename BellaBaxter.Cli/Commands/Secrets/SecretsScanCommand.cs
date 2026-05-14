using System.Diagnostics;
using System.Text;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Secrets;

public class SecretsScanSettings : CommandSettings
{
    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("-e|--env|--environment <SLUG>")]
    public string? Environment { get; init; }

    [CommandOption("--path <DIR>")]
    [System.ComponentModel.Description("Directory to scan (default: current directory)")]
    public string? Path { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class SecretsScanCommand(
    BellaClientProvider provider,
    ContextService context,
    IOutputWriter output
) : AsyncCommand<SecretsScanSettings>
{
    // Directories to skip when git is not available
    private static readonly HashSet<string> SkippedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        "node_modules",
        "vendor",
        "bin",
        "obj",
        "dist",
        "build",
        ".next",
        ".nuxt",
        "__pycache__",
        ".venv",
        "venv",
        ".pytest_cache",
        "target",
        "coverage",
        ".cache",
        "tmp",
        ".terraform",
        ".gradle",
        ".idea",
        ".vscode",
        "out",
        ".output",
        ".svelte-kit",
    };

    // Skip files larger than 2 MB
    private const long MaxFileSizeBytes = 2 * 1024 * 1024;

    protected override async Task<int> ExecuteAsync(
        CommandContext ctx,
        SecretsScanSettings settings,
        CancellationToken ct
    )
    {
        provider.ApplyOutputModeOverrides(settings.Json);

        var client = provider.CreateClient();

        var (projectSlug, _, _, envSlug, envName, _) = await context.ResolveProjectEnvironmentAsync(
            settings.Project,
            settings.Environment,
            client,
            ct,
            strictJwtLocal: true,
            bootstrapBellaFromExplicit: true
        );

        var scanPath = System.IO.Path.GetFullPath(settings.Path ?? Directory.GetCurrentDirectory());

        // ── 1. Fetch secret key names from the manifest (no values fetched) ──
        List<string> keys = [];
        await AnsiConsole
            .Status()
            .StartAsync(
                $"Fetching manifest for {projectSlug}/{envSlug}...",
                async _ =>
                {
                    var manifest = await client
                        .Api.V1.Projects[projectSlug]
                        .Environments[envSlug]
                        .Secrets.Manifest.GetAsync(cancellationToken: ct);

                    keys =
                        manifest
                            ?.Secrets?.Select(s => s.Key)
                            .Where(k => !string.IsNullOrWhiteSpace(k))
                            .Select(k => k!)
                            .ToList()
                        ?? [];
                }
            );

        if (keys.Count == 0)
        {
            output.WriteInfo($"No secrets found in {projectSlug}/{envName}.");
            return 0;
        }

        // ── 2. Collect files to scan ──────────────────────────────────────────
        List<string> files = [];
        var usingGit = false;

        await AnsiConsole
            .Status()
            .StartAsync(
                $"Listing files in {Markup.Escape(scanPath)}...",
                async _ =>
                {
                    (files, usingGit) = await GetFilesAsync(scanPath, ct);
                }
            );

        if (files.Count == 0)
        {
            output.WriteInfo("No files found to scan.");
            return 0;
        }

        // ── 3. Scan files for each key ────────────────────────────────────────
        // key → list of relative file paths where it appears
        var findings = keys.ToDictionary(k => k, _ => new List<string>());

        await AnsiConsole
            .Status()
            .StartAsync(
                $"Scanning {files.Count} file(s)...",
                async _ =>
                {
                    foreach (var file in files)
                    {
                        ct.ThrowIfCancellationRequested();

                        var content = await TryReadTextAsync(file);
                        if (content is null)
                            continue;

                        foreach (var key in keys)
                        {
                            if (content.Contains(key, StringComparison.Ordinal))
                            {
                                var rel = System.IO.Path.GetRelativePath(scanPath, file);
                                findings[key].Add(rel);
                            }
                        }
                    }
                }
            );

        // ── 4. Render ─────────────────────────────────────────────────────────
        if (settings.Json)
        {
            var obj = findings.Select(kv => new
            {
                key = kv.Key,
                found = kv.Value.Count > 0,
                files = kv.Value,
            });
            output.WriteObject(obj);
            return 0;
        }

        RenderTable(findings, projectSlug, envName, scanPath, files.Count, usingGit);

        var unusedCount = findings.Values.Count(f => f.Count == 0);
        return unusedCount > 0 ? 1 : 0;
    }

    private static void RenderTable(
        Dictionary<string, List<string>> findings,
        string projectSlug,
        string envName,
        string scanPath,
        int fileCount,
        bool usingGit
    )
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title(
                $"[bold]Secret Usage Scan — {Markup.Escape(projectSlug)}/{Markup.Escape(envName)}[/]"
            )
            .AddColumn(new TableColumn("[bold]KEY[/]").LeftAligned())
            .AddColumn(new TableColumn("[bold]STATUS[/]").Centered())
            .AddColumn(new TableColumn("[bold]FOUND IN[/]").LeftAligned());

        foreach (var (key, files) in findings.OrderBy(kv => kv.Key))
        {
            string statusMarkup;
            string filesMarkup;

            if (files.Count > 0)
            {
                statusMarkup = $"[green]✓ {files.Count} file(s)[/]";
                // Show up to 3 paths, then summarise the rest
                var shown = files.Take(3).Select(f => $"[dim]{Markup.Escape(f)}[/]");
                filesMarkup = string.Join("\n", shown);
                if (files.Count > 3)
                    filesMarkup += $"\n[dim]… and {files.Count - 3} more[/]";
            }
            else
            {
                statusMarkup = "[yellow]⚠ not found[/]";
                filesMarkup = "[dim]—[/]";
            }

            table.AddRow(Markup.Escape(key), statusMarkup, filesMarkup);
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        var usedCount = findings.Values.Count(f => f.Count > 0);
        var unusedCount = findings.Values.Count(f => f.Count == 0);
        var filterNote = usingGit ? "[dim](git ls-files)[/]" : "[dim](directory walk)[/]";

        AnsiConsole.MarkupLine(
            $"Scanned [bold]{fileCount}[/] file(s) in [dim]{Markup.Escape(scanPath)}[/] {filterNote}"
        );
        AnsiConsole.MarkupLine(
            $"[green]✓ {usedCount} referenced[/]  [yellow]⚠ {unusedCount} not found[/]"
        );

        if (unusedCount > 0)
            AnsiConsole.MarkupLine(
                "[yellow]Keys marked ⚠ were not found in the scanned files. They may be used at runtime or in files outside this directory.[/]"
            );
    }

    // ── File collection ───────────────────────────────────────────────────────

    private static async Task<(List<string> Files, bool UsedGit)> GetFilesAsync(
        string path,
        CancellationToken ct
    )
    {
        try
        {
            var gitFiles = await GetGitFilesAsync(path, ct);
            if (gitFiles.Count > 0)
                return (gitFiles, true);
        }
        catch
        {
            // git not available or not a repo — fall through
        }

        return (GetFilesManualWalk(path), false);
    }

    private static async Task<List<string>> GetGitFilesAsync(string path, CancellationToken ct)
    {
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo(
            "git",
            "ls-files --cached --others --exclude-standard"
        )
        {
            WorkingDirectory = path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        proc.Start();

        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
            return [];

        return stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => System.IO.Path.Combine(path, f.TrimEnd('\r')))
            .Where(File.Exists)
            .ToList();
    }

    private static List<string> GetFilesManualWalk(string root)
    {
        var result = new List<string>();

        void Walk(string dir)
        {
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (SkippedDirs.Contains(System.IO.Path.GetFileName(sub)))
                    continue;
                Walk(sub);
            }

            foreach (var file in Directory.EnumerateFiles(dir))
                result.Add(file);
        }

        Walk(root);
        return result;
    }

    // ── File reading ──────────────────────────────────────────────────────────

    private static async Task<string?> TryReadTextAsync(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileSizeBytes)
                return null;

            // Detect binary by checking for null bytes in first 8KB
            await using var stream = File.OpenRead(path);
            var probe = new byte[Math.Min(8192, (int)info.Length)];
            var read = await stream.ReadAsync(probe);
            if (probe.AsSpan(0, read).Contains((byte)0))
                return null;

            stream.Seek(0, SeekOrigin.Begin);
            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true
            );
            return await reader.ReadToEndAsync();
        }
        catch
        {
            return null;
        }
    }
}
