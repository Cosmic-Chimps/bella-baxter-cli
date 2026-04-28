using BellaBaxter.Client;
using BellaBaxter.Client.Models;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Pki;

public class CreatePkiRoleSettings : CommandSettings
{
    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("-e|--env|--environment <SLUG>")]
    public string? Environment { get; init; }

    [CommandOption("--name <NAME>")]
    [System.ComponentModel.Description("Role name (e.g. web-tls, internal-services)")]
    public string? Name { get; init; }

    [CommandOption("--allowed-domains <DOMAINS>")]
    [System.ComponentModel.Description("Comma-separated allowed domains (e.g. example.com,*.internal)")]
    public string? AllowedDomains { get; init; }

    [CommandOption("--allow-subdomains")]
    [System.ComponentModel.Description("Allow subdomain certificates")]
    public bool AllowSubdomains { get; init; }

    [CommandOption("--allow-any-name")]
    [System.ComponentModel.Description("Allow any common name (use with caution)")]
    public bool AllowAnyName { get; init; }

    [CommandOption("--allow-localhost")]
    [System.ComponentModel.Description("Allow localhost certificates")]
    public bool AllowLocalhost { get; init; }

    [CommandOption("--max-ttl <TTL>")]
    [System.ComponentModel.Description("Maximum certificate lifetime (e.g. 720h)")]
    public string? MaxTtl { get; init; }

    [CommandOption("--default-ttl <TTL>")]
    [System.ComponentModel.Description("Default certificate lifetime (e.g. 168h)")]
    public string? DefaultTtl { get; init; }

    [CommandOption("--key-type <TYPE>")]
    [System.ComponentModel.Description("Key type: rsa or ec (default: rsa)")]
    public string? KeyType { get; init; }

    [CommandOption("--key-bits <BITS>")]
    [System.ComponentModel.Description("Key size in bits")]
    public int? KeyBits { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class CreatePkiRoleCommand(BellaClientProvider provider, ContextService context, IOutputWriter output)
    : AsyncCommand<CreatePkiRoleSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext ctx, CreatePkiRoleSettings settings, CancellationToken ct)
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
                    output.WriteError("--name is required.");
                    return 1;
                }
                name = AnsiConsole.Ask<string>("[bold]Role name:[/]");
            }

            await AnsiConsole.Status().StartAsync($"Creating PKI role '{name}'...", async _ =>
            {
                await client.Api.V1.Projects[projectSlug].Environments[envSlug].Pki.Roles.PostAsync(
                    new PkiCreateRoleRequest
                    {
                        Name = name,
                        AllowedDomains = settings.AllowedDomains,
                        AllowSubdomains = settings.AllowSubdomains ? true : null,
                        AllowAnyName = settings.AllowAnyName ? true : null,
                        AllowLocalhost = settings.AllowLocalhost ? true : null,
                        MaxTtl = settings.MaxTtl,
                        DefaultTtl = settings.DefaultTtl,
                        KeyType = settings.KeyType,
                        KeyBits = settings.KeyBits
                    },
                    cancellationToken: ct);
            });

            output.WriteSuccess($"PKI role '{name}' created.");
            AnsiConsole.MarkupLine($"[dim]Issue a certificate: [bold]bella pki issue --role {name} --cn example.com[/][/]");

            return 0;
        }
        catch (InvalidOperationException ex)
        {
            output.WriteError(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to create PKI role: {ex.Message}");
            return 1;
        }
    }
}
