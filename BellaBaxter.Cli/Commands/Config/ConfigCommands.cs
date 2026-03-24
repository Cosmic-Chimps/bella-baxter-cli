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

    public override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var configFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "bella-cli", "config.json");

        var obj = new Dictionary<string, object?>
        {
            ["apiUrl"] = configService.ApiUrl,
            ["configFile"] = configFile,
        };

        if (output is HumanOutputWriter)
        {
            AnsiConsole.MarkupLine("[bold blue]⚙️  Bella CLI Configuration[/]");
            AnsiConsole.MarkupLine("[dim]" + new string('─', 60) + "[/]");
            AnsiConsole.MarkupLine($"[white]Server URL:[/] [green]{Markup.Escape(configService.ApiUrl)}[/]");
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

    public override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var url = settings.Url.TrimEnd('/');

        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            output.WriteError($"Invalid URL: {url}");
            return Task.FromResult(1);
        }

        configService.SetApiUrl(url);

        output.WriteSuccess($"Server URL set to: {url}");
        output.WriteInfo("Keycloak config will be auto-discovered from this server on next login.");
        return Task.FromResult(0);
    }
}
