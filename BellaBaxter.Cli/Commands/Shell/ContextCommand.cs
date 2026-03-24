using System.Text.Json;
using BellaBaxter.Client;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Shell;

// ─── Settings ────────────────────────────────────────────────────────────────

public class ContextSettings : CommandSettings
{
    [CommandOption("-q|--quiet")]
    [System.ComponentModel.Description("Suppress all error output (for prompt use)")]
    public bool Quiet { get; init; }

    [CommandOption("--json")]
    [System.ComponentModel.Description("Output context as JSON")]
    public bool Json { get; init; }
}

// ─── bella context ────────────────────────────────────────────────────────────
// Outputs the active project/environment for use in shell prompts.
// Detection order: .bella file in cwd → walk up parents → global default → exit 2.
// Zero network calls; reads local files only. Must stay ≤ 5ms.

public class ContextCommand(ConfigService config) : Command<ContextSettings>
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public override int Execute(
        CommandContext context,
        ContextSettings settings,
        CancellationToken ct
    )
    {
        var (project, env, source) = ResolveContext(config);

        if (project == null || env == null)
        {
            if (!settings.Quiet)
                Console.Error.WriteLine(
                    "No Bella context found. Run 'bella context init' in your project directory."
                );
            return 2;
        }

        if (settings.Json)
        {
            var obj = new
            {
                project,
                env,
                source,
            };
            Console.WriteLine(JsonSerializer.Serialize(obj, JsonOpts));
        }
        else
        {
            Console.Write($"{project}/{env}");
        }

        return 0;
    }

    /// <summary>
    /// Resolve the *directory-scoped* context for prompt display and context commands.
    ///
    /// Priority:
    ///   1. BELLA_BAXTER_PROJECT + BELLA_BAXTER_ENV env vars  → source "env"   (ephemeral session override)
    ///   2. .bella file in cwd or any parent    → source "local" (directory-pinned)
    ///
    /// The global default is intentionally NOT included here. It exists to save typing
    /// when running commands (e.g. bella secrets get without -p), but it must not leak
    /// into the prompt for unrelated directories.
    ///
    /// Commands that need a fallback should call ResolveContextWithGlobalFallback().
    /// </summary>
    internal static (string? Project, string? Env, string Source) ResolveContext(
        ConfigService config
    )
    {
        // 1. Session env vars — ephemeral, set via: bella context use <project>/<env>
        var envProject = Environment.GetEnvironmentVariable("BELLA_BAXTER_PROJECT")
                      ?? Environment.GetEnvironmentVariable("BELLA_PROJECT"); // deprecated
        var envEnv = Environment.GetEnvironmentVariable("BELLA_BAXTER_ENV")
                  ?? Environment.GetEnvironmentVariable("BELLA_ENV"); // deprecated
        if (!string.IsNullOrWhiteSpace(envProject) && !string.IsNullOrWhiteSpace(envEnv))
            return (envProject, envEnv, "env");

        // 2. Walk up directory tree looking for .bella
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            var bellaFile = Path.Combine(dir.FullName, ".bella");
            if (File.Exists(bellaFile))
            {
                var (p, e, _) = ParseBellaFile(bellaFile);
                if (p != null && e != null)
                    return (p, e, "local");
            }
            dir = dir.Parent;
        }

        return (null, null, "none");
    }

    /// <summary>
    /// Resolve context including org slug. Returns null org when .bella file is missing org field.
    /// </summary>
    internal static (string? Project, string? Env, string? Org, string Source) ResolveContextWithOrg(
        ConfigService config
    )
    {
        var envProject = Environment.GetEnvironmentVariable("BELLA_BAXTER_PROJECT")
                      ?? Environment.GetEnvironmentVariable("BELLA_PROJECT");
        var envEnv = Environment.GetEnvironmentVariable("BELLA_BAXTER_ENV")
                  ?? Environment.GetEnvironmentVariable("BELLA_ENV");
        if (!string.IsNullOrWhiteSpace(envProject) && !string.IsNullOrWhiteSpace(envEnv))
            return (envProject, envEnv, null, "env");

        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            var bellaFile = Path.Combine(dir.FullName, ".bella");
            if (File.Exists(bellaFile))
            {
                var (p, e, org) = ParseBellaFile(bellaFile);
                if (p != null && e != null)
                    return (p, e, org, "local");
            }
            dir = dir.Parent;
        }

        return (null, null, null, "none");
    }

    /// <summary>
    /// Parse a .bella file: simple key=value lines (blank lines and # comments ignored).
    /// </summary>
    internal static (string? Project, string? Env, string? Org) ParseBellaFile(string path)
    {
        string? project = null,
            env = null,
            org = null;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith('#') || !trimmed.Contains('='))
                    continue;
                var idx = trimmed.IndexOf('=');
                var key = trimmed[..idx].Trim().Trim('"').ToLowerInvariant();
                var val = trimmed[(idx + 1)..].Trim().Trim('"').Trim('\'');
                if (key == "project")
                    project = val;
                else if (key is "environment" or "env")
                    env = val;
                else if (key == "org")
                    org = val;
            }
        }
        catch
        { /* ignore unreadable files */
        }
        return (project, env, org);
    }
}

// ─── bella context show ───────────────────────────────────────────────────────

public class ContextShowCommand(ConfigService config, CredentialStore credentials) : Command<EmptyCommandSettings>
{
    public override int Execute(
        CommandContext context,
        EmptyCommandSettings settings,
        CancellationToken ct
    )
    {
        var (project, env, org, source) = ContextCommand.ResolveContextWithOrg(config);

        AnsiConsole.MarkupLine("[bold]Bella Context[/]");
        AnsiConsole.MarkupLine(new string('─', 40));

        if (project == null || env == null)
        {
            AnsiConsole.MarkupLine("[yellow]No directory context.[/]");
            AnsiConsole.MarkupLine(
                "[dim]Context is directory-scoped: no [cyan].bella[/][dim] file found here or in any parent.[/]"
            );
            AnsiConsole.WriteLine();

            AnsiConsole.MarkupLine("To set a directory context: [cyan]bella context init[/]");
            AnsiConsole.MarkupLine(
                "To set a session context:   [cyan]bella context use <project>/<env>[/]  [dim](sets $BELLA_BAXTER_PROJECT/$BELLA_BAXTER_ENV)[/]"
            );
            return 2;
        }

        // Org context
        var currentOrgSlug = credentials.LoadTokens()?.OrgSlug;
        var isApiKeyMode = credentials.IsApiKeyMode();
        if (org != null)
        {
            AnsiConsole.MarkupLine($"[white]Org:[/]         [cyan]{Markup.Escape(org)}[/]");
            if (currentOrgSlug != null && !string.Equals(org, currentOrgSlug, StringComparison.OrdinalIgnoreCase))
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]⚠ Warning:[/] [dim].bella org '{Markup.Escape(org)}' differs from active org '{Markup.Escape(currentOrgSlug)}'. Run [cyan]bella context init[/] to update.[/]"
                );
            }
        }
        else if (source == "local" && !isApiKeyMode)
        {
            // Only warn about missing org for OAuth users — API key users can't switch orgs
            AnsiConsole.MarkupLine("[yellow]⚠ No org in .bella file.[/] [dim]Run [cyan]bella context init[/] to add org context.[/]");
        }

        AnsiConsole.MarkupLine($"[white]Project:[/]     [cyan]{Markup.Escape(project)}[/]");
        AnsiConsole.MarkupLine($"[white]Environment:[/] [cyan]{Markup.Escape(env)}[/]");
        AnsiConsole.MarkupLine(
            $"[white]Source:[/]      [dim]{source switch {
            "env"   => "$BELLA_BAXTER_PROJECT/$BELLA_BAXTER_ENV (session — ephemeral)",
            "local" => "local .bella file (directory-scoped)",
            _       => source
        }}[/]"
        );

        if (source == "local")
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null)
            {
                var f = Path.Combine(dir.FullName, ".bella");
                if (File.Exists(f))
                {
                    AnsiConsole.MarkupLine($"[white]File:[/]        [dim]{Markup.Escape(f)}[/]");
                    break;
                }
                dir = dir.Parent;
            }
        }
        else if (source == "env")
        {
            AnsiConsole.MarkupLine("[dim]Clear with: unset BELLA_BAXTER_PROJECT BELLA_BAXTER_ENV[/]");
        }

        return 0;
    }
}

// ─── bella context init ───────────────────────────────────────────────────────

public class ContextInitSettings : CommandSettings
{
    [CommandArgument(0, "[project]")]
    [System.ComponentModel.Description("Project slug (omit for interactive)")]
    public string? Project { get; init; }

    [CommandArgument(1, "[environment]")]
    [System.ComponentModel.Description("Environment slug (omit for interactive)")]
    public string? Environment { get; init; }
}

public class ContextInitCommand(
    BellaClientProvider provider,
    CredentialStore credentials,
    KeyContextService keyContext,
    IOutputWriter output
) : AsyncCommand<ContextInitSettings>
{
    public override async Task<int> ExecuteAsync(
        CommandContext context,
        ContextInitSettings settings,
        CancellationToken ct
    )
    {
        string project,
            env;
        string? org = null;

        // ── API key fast-path ─────────────────────────────────────────────────
        // API keys are already scoped to a project+environment — skip the wizard.
        if (settings.Project == null && settings.Environment == null && credentials.IsApiKeyMode())
        {
            AnsiConsole.MarkupLine("[bold]Set Bella context for this directory[/]");
            AnsiConsole.MarkupLine(
                "[dim]This creates a [cyan].bella[/] file in the current directory.[/]"
            );
            AnsiConsole.WriteLine();

            KeyContextService.KeyContext? ctx = null;
            await AnsiConsole
                .Status()
                .StartAsync(
                    "Resolving context from API key...",
                    async _ =>
                    {
                        ctx = await keyContext.DiscoverAsync(ct);
                    }
                );

            if (ctx is null)
            {
                output.WriteError(
                    "Could not resolve context from API key. "
                        + "Check your API URL (bella config set-server api-url <url>) and that the server is reachable."
                );
                return 1;
            }

            project = ctx.ProjectSlug;
            env = ctx.EnvironmentSlug;
            org = ctx.OrgSlug;

            if (org is not null)
                AnsiConsole.MarkupLine(
                    $"  [dim]→ Org:[/]         [cyan]{Markup.Escape(ctx.OrgName ?? org)}[/] [dim]({Markup.Escape(org)})[/]"
                );
            AnsiConsole.MarkupLine(
                $"  [dim]→ Project:[/]     [cyan]{Markup.Escape(ctx.ProjectName)}[/] [dim]({Markup.Escape(project)})[/]"
            );
            if (env is not null)
                AnsiConsole.MarkupLine(
                    $"  [dim]→ Environment:[/] [cyan]{Markup.Escape(ctx.EnvironmentName ?? env)}[/] [dim]({Markup.Escape(env)})[/]"
                );
            AnsiConsole.MarkupLine($"  [dim]→ Role:[/]        [cyan]{Markup.Escape(ctx.Role)}[/]");
            AnsiConsole.WriteLine();
        }
        else if (settings.Project != null && settings.Environment != null)
        {
            // Both provided as arguments — use directly
            project = settings.Project;
            env = settings.Environment;
        }
        else
        {
            // Interactive wizard — fetch real projects and environments
            AnsiConsole.MarkupLine("[bold]Set Bella context for this directory[/]");
            AnsiConsole.MarkupLine(
                "[dim]This creates a [cyan].bella[/] file in the current directory.[/]"
            );
            AnsiConsole.WriteLine();

            BellaClient client;
            try
            {
                client = provider.CreateClient();
            }
            catch (InvalidOperationException)
            {
                output.WriteError("Not logged in. Run 'bella login' first.");
                return 1;
            }

            // Step 1: Pick a project
            List<BellaBaxter.Client.Models.ProjectResponse> projects = [];
            await AnsiConsole
                .Status()
                .StartAsync(
                    "Fetching projects...",
                    async _ =>
                    {
                        var page = await client.Api.V1.Projects.GetAsync(
                            q =>
                            {
                                q.QueryParameters.Size = 100;
                                q.QueryParameters.SortBy = "name";
                                q.QueryParameters.SortDir = "asc";
                            },
                            cancellationToken: ct
                        );
                        projects = page?.Content ?? [];
                    }
                );

            if (projects.Count == 0)
            {
                output.WriteError(
                    "No projects found. Create a project first with 'bella projects create'."
                );
                return 1;
            }

            var projectChoices = projects
                .Select(p => $"{p.Name} [dim]({Markup.Escape(p.Slug ?? "")})[/]")
                .ToList();

            var selectedProject = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[white]Select a project:[/]")
                    .PageSize(15)
                    .HighlightStyle(new Style(foreground: Color.Cyan1))
                    .AddChoices(projectChoices)
            );

            var projectIndex = projectChoices.IndexOf(selectedProject);
            var chosenProject = projects[projectIndex];
            project = chosenProject.Slug ?? chosenProject.Name ?? "";

            AnsiConsole.MarkupLine($"  [dim]→ Project:[/] [cyan]{Markup.Escape(project)}[/]");
            AnsiConsole.WriteLine();

            // Step 2: Pick an environment
            List<BellaBaxter.Client.Models.EnvironmentResponse> envList = [];
            await AnsiConsole
                .Status()
                .StartAsync(
                    $"Fetching environments for {chosenProject.Name}...",
                    async _ =>
                    {
                        envList =
                            await client
                                .Api.V1.Projects[project]
                                .Environments.GetAsync(cancellationToken: ct) ?? [];
                    }
                );

            if (envList.Count == 0)
            {
                output.WriteError(
                    $"No environments found in project '{project}'. Create one first."
                );
                return 1;
            }

            var envChoices = envList
                .Select(e => $"{e.Name} [dim]({Markup.Escape(e.Slug ?? "")})[/]")
                .ToList();

            var selectedEnv = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[white]Select an environment:[/]")
                    .PageSize(15)
                    .HighlightStyle(new Style(foreground: Color.Cyan1))
                    .AddChoices(envChoices)
            );

            var envIndex = envChoices.IndexOf(selectedEnv);
            var chosenEnv = envList[envIndex];
            env = chosenEnv.Slug ?? chosenEnv.Name ?? "";

            AnsiConsole.MarkupLine($"  [dim]→ Environment:[/] [cyan]{Markup.Escape(env)}[/]");
            AnsiConsole.WriteLine();
        }

        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(env))
        {
            output.WriteError("Project and environment cannot be empty.");
            return 1;
        }

        // Get org from API key context (set above) OR from stored OAuth tokens
        org ??= credentials.LoadTokens()?.OrgSlug;

        var bellaFile = Path.Combine(Directory.GetCurrentDirectory(), ".bella");
        var content = org != null
            ? $"org = \"{org}\"\nproject = \"{project}\"\nenvironment = \"{env}\"\n"
            : $"project = \"{project}\"\nenvironment = \"{env}\"\n";
        File.WriteAllText(bellaFile, content);

        AnsiConsole.MarkupLine(
            $"[green]✓[/] Created [cyan].bella[/] — context set to [bold]{Markup.Escape(project)}/{Markup.Escape(env)}[/]"
        );
        if (org != null)
            AnsiConsole.MarkupLine($"[dim]Org: {Markup.Escape(org)}[/]");
        AnsiConsole.MarkupLine($"[dim]File: {bellaFile}[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            "[dim]Tip: Commit .bella to share the default context with your team,[/]"
        );
        AnsiConsole.MarkupLine("[dim]     or add it to .gitignore to keep it personal.[/]");

        return 0;
    }
}

// ─── bella context clear ──────────────────────────────────────────────────────

public class ContextClearCommand : Command<EmptyCommandSettings>
{
    public override int Execute(
        CommandContext context,
        EmptyCommandSettings settings,
        CancellationToken ct
    )
    {
        var bellaFile = Path.Combine(Directory.GetCurrentDirectory(), ".bella");
        if (!File.Exists(bellaFile))
        {
            AnsiConsole.MarkupLine("[yellow]No .bella file in current directory.[/]");
            return 0;
        }

        File.Delete(bellaFile);
        AnsiConsole.MarkupLine("[green]✓[/] Removed [cyan].bella[/] from current directory.");
        return 0;
    }
}

// ─── bella context use ────────────────────────────────────────────────────────
// Sets an ephemeral session context via environment variables.
//
// Because a subprocess cannot modify the parent shell's environment, this command
// outputs eval-able shell code. The `bella shell init` snippet installs a shell
// function wrapper that intercepts `bella context use` and evals the output.
//
// Usage (after shell init):
//   bella context use camino/dev       → exports BELLA_BAXTER_PROJECT=camino BELLA_BAXTER_ENV=dev
//   bella context use                  → clears BELLA_BAXTER_PROJECT and BELLA_BAXTER_ENV

public class ContextUseSettings : CommandSettings
{
    [CommandArgument(0, "[context]")]
    [System.ComponentModel.Description(
        "Context in the form <project>/<env>, or empty to clear. Example: camino/dev"
    )]
    public string? Context { get; init; }

    [CommandOption("--shell")]
    [System.ComponentModel.Description("Shell syntax for output: bash (default), fish, powershell")]
    public string Shell { get; init; } = "bash";
}

public class ContextUseCommand : Command<ContextUseSettings>
{
    public override int Execute(
        CommandContext context,
        ContextUseSettings settings,
        CancellationToken ct
    )
    {
        var shell = settings.Shell.ToLowerInvariant().Replace("-", "").Replace("_", "");

        // Empty arg → clear session context
        if (string.IsNullOrWhiteSpace(settings.Context))
        {
            Console.WriteLine(
                shell switch
                {
                    "fish" => "set -e BELLA_BAXTER_PROJECT; set -e BELLA_BAXTER_ENV; set -e BELLA_PROJECT; set -e BELLA_ENV;",
                    "powershell" or "pwsh" =>
                        "Remove-Item Env:BELLA_BAXTER_PROJECT -ErrorAction SilentlyContinue; Remove-Item Env:BELLA_BAXTER_ENV -ErrorAction SilentlyContinue; Remove-Item Env:BELLA_PROJECT -ErrorAction SilentlyContinue; Remove-Item Env:BELLA_ENV -ErrorAction SilentlyContinue;",
                    _ => "unset BELLA_BAXTER_PROJECT; unset BELLA_BAXTER_ENV; unset BELLA_PROJECT; unset BELLA_ENV;",
                }
            );
            return 0;
        }

        // Parse "project/env" or "project env"
        string project,
            env;
        var parts = settings.Context.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 2)
        {
            project = parts[0];
            env = parts[1];
        }
        else
        {
            // Support "project env" (space-separated) passed as a single arg
            AnsiConsole.MarkupLine(
                "[red]Context must be in the form <project>/<env>. Example: camino/dev[/]"
            );
            return 1;
        }

        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(env))
        {
            AnsiConsole.MarkupLine("[red]Project and environment cannot be empty.[/]");
            return 1;
        }

        Console.WriteLine(
            shell switch
            {
                "fish" => $"set -x BELLA_BAXTER_PROJECT {project}; set -x BELLA_BAXTER_ENV {env}; set -x BELLA_PROJECT {project}; set -x BELLA_ENV {env};",
                "powershell" or "pwsh" =>
                    $"$env:BELLA_BAXTER_PROJECT = \"{project}\"; $env:BELLA_BAXTER_ENV = \"{env}\"; $env:BELLA_PROJECT = \"{project}\"; $env:BELLA_ENV = \"{env}\";",
                _ => $"export BELLA_BAXTER_PROJECT={project}; export BELLA_BAXTER_ENV={env}; export BELLA_PROJECT={project}; export BELLA_ENV={env};",
            }
        );

        return 0;
    }
}
