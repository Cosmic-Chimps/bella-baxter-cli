using BellaBaxter.Client;
using BellaBaxter.Client.Models;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Pki;

public class IssuePkiCertSettings : CommandSettings
{
    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("-e|--environment <SLUG>")]
    public string? Environment { get; init; }

    [CommandOption("-r|--role <ROLE>")]
    [System.ComponentModel.Description("PKI role name (auto-selected if only one exists)")]
    public string? Role { get; init; }

    [CommandOption("--cn <COMMON_NAME>")]
    [System.ComponentModel.Description("Certificate common name / primary domain")]
    public string? CommonName { get; init; }

    [CommandOption("--alt-names <NAMES>")]
    [System.ComponentModel.Description("Comma-separated SANs (e.g. www.example.com,api.example.com)")]
    public string? AltNames { get; init; }

    [CommandOption("--ip-sans <IPS>")]
    [System.ComponentModel.Description("Comma-separated IP SANs (e.g. 10.0.0.1,192.168.1.1)")]
    public string? IpSans { get; init; }

    [CommandOption("--ttl <TTL>")]
    [System.ComponentModel.Description("Certificate lifetime (e.g. 720h, 30d)")]
    public string? Ttl { get; init; }

    [CommandOption("--out <PREFIX>")]
    [System.ComponentModel.Description("Output file prefix (writes <prefix>.crt, <prefix>.key, <prefix>-chain.pem)")]
    public string? Out { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class IssuePkiCertCommand(BellaClientProvider provider, ContextService context, IOutputWriter output)
    : AsyncCommand<IssuePkiCertSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext ctx, IssuePkiCertSettings settings, CancellationToken ct)
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

            // Resolve role
            var roleName = settings.Role;
            if (string.IsNullOrWhiteSpace(roleName))
            {
                PkiRolesResponse? rolesResp = null;
                await AnsiConsole.Status().StartAsync("Fetching available PKI roles...", async _ =>
                {
                    rolesResp = await client.Api.V1.Projects[projectSlug].Environments[envSlug].Pki.Roles.GetAsync(cancellationToken: ct);
                });

                var roleList = rolesResp?.Roles ?? [];
                if (roleList.Count == 0)
                {
                    output.WriteError("No PKI roles found. Create one first with 'bella pki roles create'.");
                    return 1;
                }

                if (roleList.Count == 1)
                {
                    roleName = roleList[0].Name!;
                }
                else
                {
                    if (Console.IsOutputRedirected || output is JsonOutputWriter)
                    {
                        output.WriteError("--role is required in non-interactive mode.");
                        return 1;
                    }
                    roleName = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("[bold]Select PKI role:[/]")
                            .AddChoices(roleList.Select(r => r.Name ?? "")));
                }
            }

            // CN is required
            var commonName = settings.CommonName;
            if (string.IsNullOrWhiteSpace(commonName))
            {
                if (Console.IsOutputRedirected || output is JsonOutputWriter)
                {
                    output.WriteError("--cn (common name) is required.");
                    return 1;
                }
                commonName = AnsiConsole.Ask<string>("[bold]Common name (domain):[/]");
            }

            PkiIssuedCertificateResponse? result = null;
            await AnsiConsole.Status().StartAsync($"Issuing certificate for {commonName}...", async _ =>
            {
                result = await client.Api.V1.Projects[projectSlug].Environments[envSlug].Pki.Issue.PostAsync(
                    new PkiIssueCertificateRequest
                    {
                        RoleName = roleName,
                        CommonName = commonName,
                        AltNames = settings.AltNames,
                        IpSans = settings.IpSans,
                        Ttl = settings.Ttl
                    },
                    cancellationToken: ct);
            });

            if (result?.Certificate is null)
            {
                output.WriteError("No certificate returned from server.");
                return 1;
            }

            if (!string.IsNullOrWhiteSpace(settings.Out))
            {
                var prefix = settings.Out;
                await File.WriteAllTextAsync($"{prefix}.crt", result.Certificate + "\n", ct);
                await File.WriteAllTextAsync($"{prefix}.key", (result.PrivateKey ?? "") + "\n", ct);
                if (result.CaChain?.Count > 0)
                    await File.WriteAllTextAsync($"{prefix}-chain.pem", string.Join("\n", result.CaChain) + "\n", ct);

                AnsiConsole.MarkupLine($"[green]✓[/] Certificate:  [bold]{prefix}.crt[/]");
                AnsiConsole.MarkupLine($"[green]✓[/] Private key:  [bold]{prefix}.key[/]");
                if (result.CaChain?.Count > 0)
                    AnsiConsole.MarkupLine($"[green]✓[/] CA chain:     [bold]{prefix}-chain.pem[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("[bold]Certificate:[/]");
                AnsiConsole.MarkupLine(Markup.Escape(result.Certificate));

                if (!string.IsNullOrWhiteSpace(result.PrivateKey))
                {
                    AnsiConsole.MarkupLine("\n[bold]Private Key:[/]");
                    AnsiConsole.MarkupLine(Markup.Escape(result.PrivateKey));
                }
            }

            if (!string.IsNullOrWhiteSpace(result.SerialNumber))
                AnsiConsole.MarkupLine($"\n[dim]Serial:  {result.SerialNumber}[/]");

            if (result.Expiration.HasValue)
            {
                var exp = DateTimeOffset.FromUnixTimeSeconds(result.Expiration.Value).LocalDateTime;
                AnsiConsole.MarkupLine($"[dim]Expires: {exp:yyyy-MM-dd HH:mm} local[/]");
            }

            if (!string.IsNullOrWhiteSpace(result.Instructions))
                AnsiConsole.MarkupLine($"\n[dim]{Markup.Escape(result.Instructions)}[/]");

            return 0;
        }
        catch (InvalidOperationException ex)
        {
            output.WriteError(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to issue certificate: {ex.Message}");
            return 1;
        }
    }
}
