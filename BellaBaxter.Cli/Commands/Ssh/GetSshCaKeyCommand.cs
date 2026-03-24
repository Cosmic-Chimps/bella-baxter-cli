using BellaBaxter.Client;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Ssh;

public class GetSshCaKeySettings : CommandSettings
{
    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("-e|--environment <SLUG>")]
    public string? Environment { get; init; }

    [CommandOption("-o|--output <FILE>")]
    public string? OutputFile { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class GetSshCaKeyCommand(BellaClientProvider provider, ContextService context, IOutputWriter output)
    : AsyncCommand<GetSshCaKeySettings>
{
    public override async Task<int> ExecuteAsync(CommandContext ctx, GetSshCaKeySettings settings, CancellationToken ct)
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

            BellaBaxter.Client.Models.SshCaPublicKeyResponse? result = null;
            await AnsiConsole.Status().StartAsync($"Fetching SSH CA public key for {envName}...", async _ =>
            {
                result = await client.Api.V1.Projects[projectSlug].Environments[envSlug].Ssh.CaPublicKey.GetAsync(cancellationToken: ct);
            });

            if (result is null)
            {
                output.WriteError("No CA public key returned. Has SSH been configured? Run 'bella ssh configure' first.");
                return 1;
            }

            AnsiConsole.MarkupLine("\n[bold]CA Public Key:[/]");
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(result.CaPublicKey ?? "")}[/]");

            if (!string.IsNullOrWhiteSpace(result.Instructions))
            {
                AnsiConsole.MarkupLine("\n[dim]Instructions:[/]");
                AnsiConsole.WriteLine(result.Instructions);
            }

            if (!string.IsNullOrWhiteSpace(result.TerraformSnippet))
            {
                AnsiConsole.MarkupLine("\n[dim]Terraform snippet:[/]");
                AnsiConsole.MarkupLine($"[cyan]{Markup.Escape(result.TerraformSnippet)}[/]");
            }

            if (!string.IsNullOrWhiteSpace(result.AnsibleSnippet))
            {
                AnsiConsole.MarkupLine("\n[dim]Ansible snippet:[/]");
                AnsiConsole.MarkupLine($"[cyan]{Markup.Escape(result.AnsibleSnippet)}[/]");
            }

            if (!string.IsNullOrWhiteSpace(settings.OutputFile))
            {
                await File.WriteAllTextAsync(settings.OutputFile, (result.CaPublicKey ?? "") + "\n", ct);
                output.WriteSuccess($"CA public key written to {settings.OutputFile}");
            }

            return 0;
        }
        catch (InvalidOperationException ex)
        {
            output.WriteError(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to fetch SSH CA public key: {ex.Message}");
            return 1;
        }
    }
}
