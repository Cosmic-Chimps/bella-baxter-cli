using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Shell;

// ─── bella env ────────────────────────────────────────────────────────────────
// Outputs eval-able shell export statements for all Bella connection variables:
//   BELLA_API_KEY   — from stored credentials (API key or OAuth token)
//   BELLA_PROJECT   — from context (.bella file, $BELLA_PROJECT, or global default)
//   BELLA_ENV       — from context (.bella file, $BELLA_ENV, or global default)
//   BELLA_API_URL   — from stored config (only when non-default)
//
// Usage:
//   eval $(bella env)               # activate Bella context in current shell
//   eval $(bella env camino/dev)    # activate specific project/env
//   bella env --unset               # output unset/Remove-Item statements
//
// Add to your shell profile for convenience:
//   alias benv='eval $(bella env)'

public class EnvSettings : CommandSettings
{
    [CommandArgument(0, "[context]")]
    [System.ComponentModel.Description(
        "Optional context override: <project>/<env>. Falls back to .bella file / global default."
    )]
    public string? Context { get; init; }

    [CommandOption("--shell")]
    [System.ComponentModel.Description("Shell syntax: bash (default), fish, powershell")]
    public string Shell { get; init; } = "bash";

    [CommandOption("--unset")]
    [System.ComponentModel.Description("Output unset/Remove-Item statements to clear all Bella vars.")]
    public bool Unset { get; init; }

    [CommandOption("--json")]
    [System.ComponentModel.Description("Output as JSON (key-value pairs, for scripting).")]
    public bool Json { get; init; }
}

public class EnvCommand(ConfigService config, CredentialStore credentials) : Command<EnvSettings>
{
    private const string DefaultApiUrl = "https://api.bella-baxter.io";

    public override int Execute(CommandContext context, EnvSettings settings, CancellationToken ct)
    {
        var shell = settings.Shell.ToLowerInvariant().Replace("-", "").Replace("_", "");

        // ── Unset mode ───────────────────────────────────────────────────────
        if (settings.Unset)
        {
            Console.WriteLine(shell switch
            {
                "fish" => "set -e BELLA_API_KEY; set -e BELLA_PROJECT; set -e BELLA_ENV; set -e BELLA_API_URL;",
                "powershell" or "pwsh" =>
                    "Remove-Item Env:BELLA_API_KEY -ErrorAction SilentlyContinue; " +
                    "Remove-Item Env:BELLA_PROJECT -ErrorAction SilentlyContinue; " +
                    "Remove-Item Env:BELLA_ENV -ErrorAction SilentlyContinue; " +
                    "Remove-Item Env:BELLA_API_URL -ErrorAction SilentlyContinue;",
                _ => "unset BELLA_API_KEY; unset BELLA_PROJECT; unset BELLA_ENV; unset BELLA_API_URL;",
            });
            return 0;
        }

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
                Console.Error.WriteLine("bella env: context must be in the form <project>/<env>. Example: camino/dev");
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
            Console.Error.WriteLine(
                "bella env: no project/environment context found.\n" +
                "  Set one with: bella context init\n" +
                "  Or specify directly: bella env <project>/<env>"
            );
            return 1;
        }

        // ── Resolve API key ──────────────────────────────────────────────────
        var storedKey = credentials.LoadApiKey();
        if (storedKey == null)
        {
            Console.Error.WriteLine(
                "bella env: no API key stored. Log in first:\n" +
                "  bella login --api-key bax-<keyId>-<secret>"
            );
            return 1;
        }

        var apiKey = storedKey.Raw;
        var apiUrl = config.ApiUrl;

        // ── JSON mode ────────────────────────────────────────────────────────
        if (settings.Json)
        {
            var includeUrl = apiUrl != DefaultApiUrl;
            var obj = new System.Collections.Generic.Dictionary<string, string>
            {
                ["BELLA_BAXTER_API_KEY"] = apiKey,
                ["BELLA_BAXTER_PROJECT"] = project,
                ["BELLA_BAXTER_ENV"]     = env,
                // Deprecated aliases
                ["BELLA_API_KEY"] = apiKey,
                ["BELLA_PROJECT"] = project,
                ["BELLA_ENV"]     = env,
            };
            if (includeUrl)
            {
                obj["BELLA_BAXTER_URL"] = apiUrl;
                obj["BELLA_API_URL"] = apiUrl; // deprecated alias
            }

            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(obj,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        // ── Shell export mode ────────────────────────────────────────────────
        Console.WriteLine(BuildExportStatement(shell, apiKey, project, env, apiUrl));
        return 0;
    }

    private static string BuildExportStatement(
        string shell, string apiKey, string project, string env, string apiUrl)
    {
        var includeUrl = apiUrl != DefaultApiUrl;

        return shell switch
        {
            "fish" => includeUrl
                ? $"set -x BELLA_BAXTER_API_KEY {apiKey}; set -x BELLA_BAXTER_PROJECT {project}; set -x BELLA_BAXTER_ENV {env}; set -x BELLA_BAXTER_URL {apiUrl}; set -x BELLA_API_KEY {apiKey}; set -x BELLA_PROJECT {project}; set -x BELLA_ENV {env}; set -x BELLA_API_URL {apiUrl};"
                : $"set -x BELLA_BAXTER_API_KEY {apiKey}; set -x BELLA_BAXTER_PROJECT {project}; set -x BELLA_BAXTER_ENV {env}; set -x BELLA_API_KEY {apiKey}; set -x BELLA_PROJECT {project}; set -x BELLA_ENV {env};",

            "powershell" or "pwsh" => includeUrl
                ? $"$env:BELLA_BAXTER_API_KEY = \"{apiKey}\"; $env:BELLA_BAXTER_PROJECT = \"{project}\"; $env:BELLA_BAXTER_ENV = \"{env}\"; $env:BELLA_BAXTER_URL = \"{apiUrl}\"; $env:BELLA_API_KEY = \"{apiKey}\"; $env:BELLA_PROJECT = \"{project}\"; $env:BELLA_ENV = \"{env}\"; $env:BELLA_API_URL = \"{apiUrl}\";"
                : $"$env:BELLA_BAXTER_API_KEY = \"{apiKey}\"; $env:BELLA_BAXTER_PROJECT = \"{project}\"; $env:BELLA_BAXTER_ENV = \"{env}\"; $env:BELLA_API_KEY = \"{apiKey}\"; $env:BELLA_PROJECT = \"{project}\"; $env:BELLA_ENV = \"{env}\";",

            _ => includeUrl
                ? $"export BELLA_BAXTER_API_KEY={apiKey}; export BELLA_BAXTER_PROJECT={project}; export BELLA_BAXTER_ENV={env}; export BELLA_BAXTER_URL={apiUrl}; export BELLA_API_KEY={apiKey}; export BELLA_PROJECT={project}; export BELLA_ENV={env}; export BELLA_API_URL={apiUrl};"
                : $"export BELLA_BAXTER_API_KEY={apiKey}; export BELLA_BAXTER_PROJECT={project}; export BELLA_BAXTER_ENV={env}; export BELLA_API_KEY={apiKey}; export BELLA_PROJECT={project}; export BELLA_ENV={env};",
        };
    }
}
