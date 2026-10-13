using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Config;

// ── bella config show ─────────────────────────────────────────────────────────

public class ConfigShowCommand(ConfigService configService, IOutputWriter output)
    : AsyncCommand<ConfigShowCommand.Settings>
{
    public class Settings : CommandSettings { }

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var configFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "bella-cli", "config.json");

        var obj = new Dictionary<string, object?>
        {
            // Display only: the value as configured, even when the https rule refuses it — this is where
            // an operator comes to find out what is wrong (backlog §2.31 follow-up).
            ["apiUrl"] = configService.ApiUrlAsConfigured,
            ["apiUrlProblem"] = configService.ApiUrlProblem,
            ["configFile"] = configFile,
        };

        if (output is HumanOutputWriter)
        {
            AnsiConsole.MarkupLine("[bold blue]⚙️  Bella CLI Configuration[/]");
            AnsiConsole.MarkupLine("[dim]" + new string('─', 60) + "[/]");
            AnsiConsole.MarkupLine($"[white]Server URL:[/] [green]{Markup.Escape(configService.ApiUrlAsConfigured)}[/]");
            if (configService.ApiUrlProblem is { } problem)
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(problem)}[/]");
            AnsiConsole.MarkupLine("[dim]" + new string('─', 60) + "[/]");
            AnsiConsole.MarkupLine($"[dim]Config file: {configFile}[/]");
        }
        else
        {
            output.WriteObject(obj);
        }

        return Task.FromResult(0);
    }
}

// ── bella config set-server <url> ─────────────────────────────────────────────

public class ConfigSetServerCommand(ConfigService configService, IOutputWriter output)
    : AsyncCommand<ConfigSetServerCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<url>")]
        [System.ComponentModel.Description("The Bella Baxter API server URL (e.g. https://api.my-instance.com)")]
        public string Url { get; set; } = string.Empty;
    }

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var url = settings.Url.TrimEnd('/');

        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            output.WriteError($"Invalid URL: {url}");
            return Task.FromResult(1);
        }

        if (ConfigService.ProblemFor(url, "bella config set-server") is { } problem)
        {
            output.WriteError(problem);
            return Task.FromResult(1);
        }

        configService.SetApiUrl(url);

        output.WriteSuccess($"Server URL set to: {url}");
        output.WriteInfo("Keycloak config will be auto-discovered from this server on next login.");
        return Task.FromResult(0);
    }
}

// ── bella config trust <url> | --list | --remove <url> ────────────────────────
// Advisory clients-1 follow-up (cli-bella-url), security team 2026-10-10.

/// <summary>
/// Manages the servers a working-directory <c>.bella</c> may name. A <c>.bella</c> arrives with any clone,
/// so its <c>url</c> is followed only when the user has trusted that origin — here, or by answering the
/// prompt the first time. <c>BELLA_BAXTER_URL</c>, <c>config.json</c> and an origin the user already logged
/// in to need no entry.
/// </summary>
public class ConfigTrustCommand(ConfigService configService, IOutputWriter output)
    : AsyncCommand<ConfigTrustCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandArgument(0, "[url]")]
        [System.ComponentModel.Description("The server to trust (or, with --remove, to forget), e.g. https://bella.example.com")]
        public string? Url { get; set; }

        [CommandOption("--list")]
        [System.ComponentModel.Description("List the trusted servers.")]
        public bool List { get; set; }

        [CommandOption("--remove")]
        [System.ComponentModel.Description("Forget the server instead of trusting it.")]
        public bool Remove { get; set; }
    }

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct) =>
        Task.FromResult(Run(settings));

    internal int Run(Settings settings)
    {
        if (settings.List)
        {
            if (output is HumanOutputWriter && configService.TrustedOrigins.Count == 0)
                AnsiConsole.MarkupLine("[dim]No trusted servers. A .bella naming an unknown server asks before it is used.[/]");
            else if (output is HumanOutputWriter)
                foreach (var origin in configService.TrustedOrigins)
                    AnsiConsole.MarkupLine($"[cyan]{Markup.Escape(origin)}[/]");
            else
                output.WriteObject(new Dictionary<string, object?> { ["trustedOrigins"] = configService.TrustedOrigins });
            return 0;
        }

        if (string.IsNullOrWhiteSpace(settings.Url))
        {
            output.WriteError("Give the server to trust: bella config trust <url> (or --list).");
            return 1;
        }

        if (ConfigService.OriginOf(settings.Url) is null)
        {
            output.WriteError($"Invalid URL: {settings.Url}. Expected an http(s):// address.");
            return 1;
        }

        try
        {
            if (settings.Remove)
            {
                var (origin, removed) = configService.UntrustOrigin(settings.Url);
                if (removed)
                    output.WriteSuccess($"No longer trusting {origin}");
                else
                    output.WriteInfo($"{origin} was not in the trusted list.");
                return 0;
            }

            output.WriteSuccess($"Trusting {configService.TrustOrigin(settings.Url)}");
            return 0;
        }
        catch (InsecureApiAddressException ex)
        {
            output.WriteError(ex.Message);
            return 1;
        }
    }
}
