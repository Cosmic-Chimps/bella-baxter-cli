using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using System.Diagnostics;

namespace BellaCli.Commands.Shell;

// ─── bella shell open ─────────────────────────────────────────────────────────
// Two modes:
//
// 1. INTERACTIVE SHELL (no extra args)
//    Opens a subshell with BELLA_API_KEY, BELLA_PROJECT, BELLA_ENV pre-set.
//    Any tool that reads them (bella run, JS SDK, Python SDK, etc.) works
//    without extra configuration.
//
//    bella shell open                    # open subshell with current context
//    bella shell open camino/dev         # open subshell for specific project/env
//    bella shell open --shell /bin/zsh   # force a specific shell
//
// 2. RUN COMMAND (args after --)
//    Runs the command with BELLA_* vars injected — no interactive shell.
//    Perfect for running servers / scripts that use the Bella SDK middleware.
//
//    bella shell open -- node server.js
//    bella shell open camino/dev -- python app.py
//
// Exit the interactive subshell normally (exit / Ctrl-D) to return to the parent shell.

public class ShellOpenSettings : CommandSettings
{
    [CommandArgument(0, "[context]")]
    [System.ComponentModel.Description(
        "Optional context override: <project>/<env>. Falls back to .bella file / global default."
    )]
    public string? Context { get; init; }

    [CommandOption("--shell")]
    [System.ComponentModel.Description(
        "Shell to spawn (default: $SHELL on Unix, cmd.exe on Windows). Example: /bin/bash, zsh, powershell"
    )]
    public string? ShellPath { get; init; }
}

public class ShellOpenCommand(ConfigService config, CredentialStore credentials) : Command<ShellOpenSettings>
{
    private const string DefaultApiUrl = "https://api.bella-baxter.io";

    public override int Execute(CommandContext context, ShellOpenSettings settings, CancellationToken ct)
    {
        // ── Resolve project + env ────────────────────────────────────────────
        string? project = null, env = null;

        if (!string.IsNullOrWhiteSpace(settings.Context))
        {
            var parts = settings.Context.Split('/', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]) && !string.IsNullOrWhiteSpace(parts[1]))
            {
                project = parts[0];
                env = parts[1];
            }
            else
            {
                AnsiConsole.MarkupLine("[red]Context must be in the form <project>/<env>. Example: camino/dev[/]");
                return 1;
            }
        }
        else
        {
            var (resolvedProject, resolvedEnv, _) = ContextCommand.ResolveContext(config);
            project = resolvedProject;
            env = resolvedEnv;
        }

        if (project == null || env == null)
        {
            AnsiConsole.MarkupLine(
                "[red]No project/environment context found.[/]\n" +
                "  Set one with: [cyan]bella context init[/]\n" +
                "  Or specify directly: [cyan]bella shell open <project>/<env>[/]"
            );
            return 1;
        }

        // ── Resolve API key ──────────────────────────────────────────────────
        var storedKey = credentials.LoadApiKey();
        if (storedKey == null)
        {
            AnsiConsole.MarkupLine(
                "[red]No API key stored.[/] Log in first:\n" +
                "  [cyan]bella login --api-key bax-<keyId>-<secret>[/]"
            );
            return 1;
        }

        // ── Check for command to run (args after --) ─────────────────────────
        var remainingArgs = context.Remaining.Raw.ToArray();
        if (remainingArgs.Length > 0)
            return RunCommand(remainingArgs, project, env, storedKey.Raw, config.ApiUrl);

        // ── Interactive mode: determine shell executable ─────────────────────
        string shellExe;
        if (!string.IsNullOrWhiteSpace(settings.ShellPath))
            shellExe = settings.ShellPath;
        else if (OperatingSystem.IsWindows())
            shellExe = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
        else
            shellExe = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/bash";

        // ── Banner ───────────────────────────────────────────────────────────
        AnsiConsole.MarkupLine($"[bold green]✓[/] Entering Bella shell — [cyan]{Markup.Escape(project)}/{Markup.Escape(env)}[/]");
        AnsiConsole.MarkupLine($"[dim]  API key: {Markup.Escape(MaskKey(storedKey.Raw))}[/]");
        AnsiConsole.MarkupLine($"[dim]  Shell:   {Markup.Escape(shellExe)}[/]");
        AnsiConsole.MarkupLine($"[dim]  Type 'exit' or press Ctrl-D to return to the parent shell.[/]");
        AnsiConsole.WriteLine();

        var psi = BuildProcessInfo(shellExe, project, env, storedKey.Raw, config.ApiUrl);

        try
        {
            var proc = Process.Start(psi);
            if (proc == null)
            {
                AnsiConsole.MarkupLine($"[red]Failed to start shell: {Markup.Escape(shellExe)}[/]");
                return 1;
            }
            proc.WaitForExit();
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[dim]Exited Bella shell ({Markup.Escape(project)}/{Markup.Escape(env)}).[/]");
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to start shell '{Markup.Escape(shellExe)}': {Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }

    private int RunCommand(string[] args, string project, string env, string apiKey, string apiUrl)
    {
        AnsiConsole.MarkupLine($"[bold green]✓[/] Running with Bella context — [cyan]{Markup.Escape(project)}/{Markup.Escape(env)}[/]");
        AnsiConsole.MarkupLine($"[dim]  API key: {Markup.Escape(MaskKey(apiKey))}[/]");
        AnsiConsole.MarkupLine($"[dim]  Command: {Markup.Escape(string.Join(" ", args))}[/]");
        AnsiConsole.WriteLine();

        var psi = BuildProcessInfo(args[0], project, env, apiKey, apiUrl);
        foreach (var arg in args.Skip(1))
            psi.ArgumentList.Add(arg);

        try
        {
            var proc = Process.Start(psi);
            if (proc == null)
            {
                AnsiConsole.MarkupLine($"[red]Failed to start: {Markup.Escape(args[0])}[/]");
                return 1;
            }
            proc.WaitForExit();
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to run '{Markup.Escape(args[0])}': {Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }

    private ProcessStartInfo BuildProcessInfo(string exe, string project, string env, string apiKey, string apiUrl)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };

        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string k && entry.Value is string v)
                psi.Environment[k] = v;
        }

        psi.Environment["BELLA_BAXTER_API_KEY"] = apiKey;
        psi.Environment["BELLA_BAXTER_PROJECT"] = project;
        psi.Environment["BELLA_BAXTER_ENV"] = env;
        psi.Environment["BELLA_BAXTER_CONTEXT"] = $"{project}/{env}";
        // Deprecated aliases for backward compatibility
        psi.Environment["BELLA_API_KEY"] = apiKey;
        psi.Environment["BELLA_PROJECT"] = project;
        psi.Environment["BELLA_ENV"] = env;
        psi.Environment["BELLA_CONTEXT"] = $"{project}/{env}";

        if (apiUrl != DefaultApiUrl)
        {
            psi.Environment["BELLA_BAXTER_URL"] = apiUrl;
            psi.Environment["BELLA_API_URL"] = apiUrl; // deprecated alias
        }

        return psi;
    }

    private static string MaskKey(string raw)
    {
        var parts = raw.Split('-');
        if (parts.Length >= 3)
            return $"bax-{parts[1]}-***";
        return "bax-***";
    }
}


