using BellaBaxter.Client;
using BellaBaxter.Client.Models;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Ssh;

public class CreateSshRoleSettings : CommandSettings
{
    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("-e|--environment <SLUG>")]
    public string? Environment { get; init; }

    [CommandOption("-n|--name <NAME>")]
    public string? Name { get; init; }

    [CommandOption("-u|--allowed-users <USERS>")]
    [System.ComponentModel.Description("Comma-separated allowed users (e.g. ec2-user,ubuntu)")]
    public string? AllowedUsers { get; init; }

    [CommandOption("--default-ttl <TTL>")]
    [System.ComponentModel.Description("Default certificate TTL (e.g. 8h)")]
    public string DefaultTtl { get; init; } = "8h";

    [CommandOption("--max-ttl <TTL>")]
    [System.ComponentModel.Description("Max certificate TTL (e.g. 24h)")]
    public string MaxTtl { get; init; } = "24h";

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class CreateSshRoleCommand(BellaClientProvider provider, ContextService context, IOutputWriter output)
    : AsyncCommand<CreateSshRoleSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext ctx, CreateSshRoleSettings settings, CancellationToken ct)
    {
        provider.ApplyOutputModeOverrides(settings.Json);

        BellaClient client;
        try { client = provider.CreateClient(); }
        catch (InvalidOperationException)
        {
            output.WriteError("Not logged in. Run 'bella login' first.");
            return 1;
        }

        try
        {
            var (projectSlug, _, _) = await context.ResolveProjectAsync(settings.Project, client, ct);
            var (envSlug, envName, _) = await context.ResolveEnvironmentAsync(settings.Environment, projectSlug, client, ct);

            var name = settings.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                if (Console.IsOutputRedirected || output is JsonOutputWriter)
                {
                    output.WriteError("--name is required in non-interactive mode.");
                    return 1;
                }
                name = AnsiConsole.Prompt(new TextPrompt<string>("Role [bold]name[/]:").PromptStyle("green"));
            }

            var allowedUsers = settings.AllowedUsers;
            if (string.IsNullOrWhiteSpace(allowedUsers))
            {
                if (Console.IsOutputRedirected || output is JsonOutputWriter)
                {
                    output.WriteError("--allowed-users is required in non-interactive mode.");
                    return 1;
                }
                allowedUsers = AnsiConsole.Prompt(
                    new TextPrompt<string>("Allowed [bold]users[/] (comma-separated):")
                        .PromptStyle("green")
                        .WithConverter(s => s));
            }

            await AnsiConsole.Status().StartAsync($"Creating SSH role '{name}' in {envName}...", async _ =>
            {
                await client.Api.V1.Projects[projectSlug].Environments[envSlug].Ssh.Roles.PostAsync(
                    new SshCreateRoleRequest
                    {
                        Name = name,
                        AllowedUsers = allowedUsers,
                        DefaultTtl = settings.DefaultTtl,
                        MaxTtl = settings.MaxTtl
                    },
                    cancellationToken: ct);
            });

            output.WriteSuccess($"SSH role '{name}' created.");
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            output.WriteError(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to create SSH role: {ex.Message}");
            return 1;
        }
    }
}
