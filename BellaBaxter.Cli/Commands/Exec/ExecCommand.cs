using System.ComponentModel;
using System.Diagnostics;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Exec;

/// <summary>
/// bella exec — inject Bella connection credentials as env vars and spawn a subprocess.
///
/// Supports two auth modes:
///
/// API key mode (bella login --api-key):
///   Injects BELLA_BAXTER_API_KEY + BELLA_BAXTER_URL. The SDK discovers project/env
///   from the key automatically via /api/v1/keys/me — no .bella config needed.
///
/// JWT mode (bella login):
///   Injects BELLA_BAXTER_ACCESS_TOKEN + BELLA_BAXTER_URL + BELLA_BAXTER_PROJECT + BELLA_BAXTER_ENV.
///   Project/env are resolved from (in order): -p/-e flags → env vars → .bella file (walks up from cwd).
///
/// Injected vars (API key mode):
///   BELLA_BAXTER_API_KEY      — stored API key (bax-… token)
///   BELLA_BAXTER_URL          — Baxter API base URL
///   BELLA_BAXTER_APP_CLIENT   — optional app name for audit logs (set with --app)
///   BELLA_API_KEY / BELLA_API_URL — deprecated aliases
///
/// Injected vars (JWT mode):
///   BELLA_BAXTER_ACCESS_TOKEN — short-lived JWT access token (refreshed if needed)
///   BELLA_BAXTER_PROJECT      — project slug (from .bella / config / -p flag)
///   BELLA_BAXTER_ENV          — environment slug (from .bella / config / -e flag)
///   BELLA_BAXTER_URL          — Baxter API base URL
///   BELLA_BAXTER_APP_CLIENT   — optional app name for audit logs (set with --app)
/// </summary>
public class ExecCommand(
    CredentialStore credentials,
    ConfigService config,
    AuthService authService,
    WorkloadIdentityService workloadIdentity,
    IOutputWriter output
) : AsyncCommand<ExecCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandOption("-p|--project <slug>")]
        [Description("Project slug — used for workload identity (GitHub Actions / Kubernetes)")]
        public string? Project { get; set; }

        [CommandOption("-e|--environment <slug>")]
        [Description("Environment slug — used for workload identity (GitHub Actions / Kubernetes)")]
        public string? Environment { get; set; }

        [CommandOption("--app <name>")]
        [Description(
            "Application name injected as BELLA_BAXTER_APP_CLIENT (useful for audit logs)"
        )]
        public string? App { get; set; }

        [CommandArgument(0, "[cmd...]")]
        [Description("Command and arguments to run (prefix with --)")]
        public string[] Cmd { get; set; } = [];
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context,
        Settings settings,
        CancellationToken ct
    )
    {
        await Task.CompletedTask; // satisfy async signature

        // Collect args — strip the bare "--" separator if present
        var args = settings.Cmd.Where(a => a != "--").ToArray();
        if (args.Length == 0 && context.Remaining.Raw.Count > 0)
            args = [.. context.Remaining.Raw.Where(a => a != "--")];

        if (args.Length == 0)
        {
            output.WriteError("No command specified.\nUsage: bella exec -- <command> [args...]");
            return 1;
        }

        // ── 1. Resolve API URL ────────────────────────────────────────────────
        var apiUrl =
            config.ApiUrl
            ?? System.Environment.GetEnvironmentVariable("BELLA_BAXTER_URL")
            ?? System.Environment.GetEnvironmentVariable("BELLA_API_URL"); // deprecated

        // ── 2. Resolve app client name ────────────────────────────────────────
        var appClient =
            settings.App ?? System.Environment.GetEnvironmentVariable("BELLA_BAXTER_APP_CLIENT");

        // ── 3. Resolve credentials — workload identity → API key → JWT ───────
        string? apiKey = null;
        string? accessToken = null;
        string? projectSlug = null;
        string? environmentSlug = null;

        var workloadResult = await workloadIdentity.TryAutoExchangeAsync(
            settings.Project,
            settings.Environment,
            ct: ct
        );

        if (workloadResult is not null)
        {
            var platform = WorkloadIdentityService.DetectPlatform();
            AnsiConsole.MarkupLine($"[dim]🔑 Using workload identity ({platform})[/]");
            apiKey = workloadResult.Token;
        }
        else if (credentials.LoadApiKey() is { } storedKey)
        {
            apiKey = storedKey.Raw;
        }
        else
        {
            apiKey = System.Environment.GetEnvironmentVariable("BELLA_BAXTER_API_KEY")
                     ?? System.Environment.GetEnvironmentVariable("BELLA_API_KEY");
        }

        if (string.IsNullOrEmpty(apiKey) && credentials.HasOAuthTokens())
        {
            // JWT mode — refresh token if needed
            try
            {
                var tokens = await authService.EnsureValidTokenAsync(ct);
                accessToken = tokens.AccessToken;
            }
            catch (Exception ex)
            {
                output.WriteError($"Failed to refresh JWT token: {ex.Message}\nRun: bella login");
                return 1;
            }

            // Resolve project/env: -p/-e flags → .bella file (walks up) → bella config defaults
            projectSlug = settings.Project
                ?? System.Environment.GetEnvironmentVariable("BELLA_BAXTER_PROJECT")
                ?? System.Environment.GetEnvironmentVariable("BELLA_PROJECT");
            environmentSlug = settings.Environment
                ?? System.Environment.GetEnvironmentVariable("BELLA_BAXTER_ENV")
                ?? System.Environment.GetEnvironmentVariable("BELLA_ENV");

            if (string.IsNullOrEmpty(projectSlug) || string.IsNullOrEmpty(environmentSlug))
            {
                var bellaCtx = ReadBellaFile(Directory.GetCurrentDirectory());
                projectSlug ??= bellaCtx?.Project;
                environmentSlug ??= bellaCtx?.Environment;
            }

            if (string.IsNullOrEmpty(projectSlug) || string.IsNullOrEmpty(environmentSlug))
            {
                output.WriteError(
                    "Project and environment are required for JWT auth.\n"
                        + "Options (in priority order):\n"
                        + "  1. bella exec -p my-project -e dev -- <command>\n"
                        + "  2. Create a .bella file: echo 'project = my-project\\nenvironment = dev' > .bella\n"
                        + "  3. Set BELLA_BAXTER_PROJECT and BELLA_BAXTER_ENV environment variables"
                );
                return 1;
            }
        }

        if (string.IsNullOrEmpty(apiKey) && string.IsNullOrEmpty(accessToken))
        {
            output.WriteError(
                "Not authenticated.\n"
                    + "Run: bella login           (JWT / interactive)\n"
                    + "     bella login --api-key  (API key)\n"
                    + "Or set: BELLA_BAXTER_API_KEY environment variable"
            );
            return 1;
        }

        // ── 4. Show what we're injecting ─────────────────────────────────────
        AnsiConsole.MarkupLine($"[dim]  API URL:  [cyan]{Markup.Escape(apiUrl ?? "")}[/][/]");
        if (!string.IsNullOrEmpty(apiKey))
        {
            AnsiConsole.MarkupLine(
                $"[dim]  API Key:  [cyan]{Markup.Escape(apiKey[..Math.Min(12, apiKey.Length)])}***[/][/]"
            );
            AnsiConsole.MarkupLine($"[dim]  Context:  resolved from API key via /api/v1/keys/me[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[dim]  Auth:     [cyan]JWT (access token)[/][/]");
            AnsiConsole.MarkupLine($"[dim]  Project:  [cyan]{Markup.Escape(projectSlug!)}[/][/]");
            AnsiConsole.MarkupLine($"[dim]  Env:      [cyan]{Markup.Escape(environmentSlug!)}[/][/]");
        }
        if (appClient is not null)
            AnsiConsole.MarkupLine($"[dim]  App:      [cyan]{Markup.Escape(appClient)}[/][/]");

        // ── 5. Spawn child with injected credentials ──────────────────────────
        var psi = new ProcessStartInfo { FileName = args[0], UseShellExecute = false };
        for (int i = 1; i < args.Length; i++)
            psi.ArgumentList.Add(args[i]);

        foreach (
            System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables()
        )
            psi.Environment[entry.Key?.ToString() ?? ""] = entry.Value?.ToString();

        // Unset all Bella-specific vars before re-injecting with fresh values.
        psi.Environment.Remove("BELLA_BAXTER_API_KEY");
        psi.Environment.Remove("BELLA_BAXTER_ACCESS_TOKEN");
        psi.Environment.Remove("BELLA_BAXTER_PROJECT");
        psi.Environment.Remove("BELLA_BAXTER_ENV");
        psi.Environment.Remove("BELLA_BAXTER_URL");
        psi.Environment.Remove("BELLA_BAXTER_APP_CLIENT");
        psi.Environment.Remove("BELLA_API_KEY");
        psi.Environment.Remove("BELLA_API_URL");

        psi.Environment["BELLA_BAXTER_URL"] = apiUrl;
        psi.Environment["BELLA_API_URL"] = apiUrl; // deprecated alias

        if (!string.IsNullOrEmpty(apiKey))
        {
            psi.Environment["BELLA_BAXTER_API_KEY"] = apiKey;
            psi.Environment["BELLA_API_KEY"] = apiKey; // deprecated alias
        }
        else
        {
            psi.Environment["BELLA_BAXTER_ACCESS_TOKEN"] = accessToken!;
            psi.Environment["BELLA_BAXTER_PROJECT"] = projectSlug!;
            psi.Environment["BELLA_BAXTER_ENV"] = environmentSlug!;
        }

        if (appClient is not null)
            psi.Environment["BELLA_BAXTER_APP_CLIENT"] = appClient;

        var process = Process.Start(psi);
        if (process is null)
        {
            output.WriteError($"Failed to start process: {args[0]}");
            return 1;
        }

        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>
    /// Walk up the directory tree from startDir looking for a .bella file.
    /// .bella file format:
    ///   project = my-project
    ///   environment = dev   # comments are ignored
    /// </summary>
    private static (string? Project, string? Environment)? ReadBellaFile(string startDir)
    {
        var dir = startDir;
        while (true)
        {
            var candidate = Path.Combine(dir, ".bella");
            if (File.Exists(candidate))
            {
                var ctx = ParseBellaFile(candidate);
                if (ctx is not null) return ctx;
            }
            var parent = Path.GetDirectoryName(dir);
            if (parent is null || parent == dir) break;
            dir = parent;
        }
        return null;
    }

    private static (string? Project, string? Environment)? ParseBellaFile(string filePath)
    {
        try
        {
            string? project = null, environment = null;
            foreach (var rawLine in File.ReadAllLines(filePath))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith('#') || !line.Contains('='))
                    continue;
                var idx = line.IndexOf('=');
                var key = line[..idx].Trim().Trim('"', '\'').ToLowerInvariant();
                var val = line[(idx + 1)..].Trim().Trim('"', '\'');
                if (key == "project") project = val;
                else if (key is "environment" or "env") environment = val;
            }
            if (project is not null || environment is not null)
                return (project, environment);
        }
        catch { /* ignore unreadable files */ }
        return null;
    }
}
