using BellaBaxter.Client;
using BellaBaxter.Client.Models;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using System.Diagnostics;

namespace BellaCli.Commands.Ssh;

public class ConnectSshSettings : CommandSettings
{
    [CommandArgument(0, "<user@host>")]
    [System.ComponentModel.Description("SSH target (e.g. ec2-user@10.0.0.1)")]
    public string Host { get; init; } = "";

    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("-e|--environment <SLUG>")]
    public string? Environment { get; init; }

    [CommandOption("-r|--role <ROLE>")]
    public string? Role { get; init; }

    [CommandOption("-k|--key <PATH>")]
    [System.ComponentModel.Description("Path to SSH private key (without .pub). Default: ~/.ssh/id_ed25519 or id_rsa")]
    public string? KeyPath { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class ConnectSshCommand(BellaClientProvider provider, ContextService context, IOutputWriter output)
    : AsyncCommand<ConnectSshSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext ctx, ConnectSshSettings settings, CancellationToken ct)
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
            var (envSlug, _, _) = await context.ResolveEnvironmentAsync(settings.Environment, projectSlug, client, ct);

            // Resolve private key path — also tries <path>.private if <path> doesn't exist
            var privKeyPath = settings.KeyPath;
            if (string.IsNullOrWhiteSpace(privKeyPath))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                foreach (var name in new[] { "id_ed25519", "id_ecdsa", "id_rsa" })
                {
                    var p = Path.Combine(home, ".ssh", name);
                    if (File.Exists(p)) { privKeyPath = p; break; }
                }
                privKeyPath ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "id_ed25519");
            }
            else
            {
                privKeyPath = SignSshKeyCommand.ResolvePrivateKeyPath(privKeyPath);
            }

            // Derive public key path — handles vee-stage.private → vee-stage.pub
            var pubKeyPath = SignSshKeyCommand.DerivePublicKeyPath(privKeyPath);

            // OpenSSH auto-discovers cert at "{identity}-cert.pub" alongside the private key.
            // We always write to this path so no -o CertificateFile= flag is needed.
            var sshCertPath = privKeyPath + "-cert.pub";

            // Also check the cert path the `sign` command writes to (e.g. vee-stage-cert.pub from vee-stage.pub)
            var signCertPath = SignSshKeyCommand.ToCertPath(pubKeyPath);

            // Find the freshest existing cert (< 30 min old) from either location
            static bool IsFresh(string path) =>
                File.Exists(path) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalSeconds < 1800;

            var needsSign = true;
            if (IsFresh(sshCertPath) || IsFresh(signCertPath))
            {
                needsSign = false;
                // Ensure cert is at sshCertPath for OpenSSH auto-discovery
                if (!IsFresh(sshCertPath) && IsFresh(signCertPath))
                    File.Copy(signCertPath, sshCertPath, overwrite: true);
                AnsiConsole.MarkupLine($"[dim]Using existing certificate (< 30 min old): {sshCertPath}[/]");
            }

            if (needsSign)
            {
                var roleName = settings.Role;
                if (string.IsNullOrWhiteSpace(roleName))
                {
                    BellaBaxter.Client.Models.SshRolesResponse? rolesResp = null;
                    await AnsiConsole.Status().StartAsync("Fetching available roles...", async _ =>
                    {
                        rolesResp = await client.Api.V1.Projects[projectSlug].Environments[envSlug].Ssh.Roles.GetAsync(cancellationToken: ct);
                    });

                    var roleList = rolesResp?.Roles ?? [];
                    if (roleList.Count == 0)
                    {
                        output.WriteError("No SSH roles found. Create one first with 'bella ssh roles create'.");
                        return 1;
                    }

                    if (roleList.Count == 1)
                    {
                        roleName = roleList[0].Name!;
                    }
                    else
                    {
                        roleName = AnsiConsole.Prompt(
                            new SelectionPrompt<string>()
                                .Title("[bold]Select SSH role:[/]")
                                .AddChoices(roleList.Select(r => r.Name ?? "")));
                    }
                }

                if (!File.Exists(pubKeyPath))
                {
                    output.WriteError($"Public key not found at {pubKeyPath}. Pass --key <path-to-private-key>.");
                    return 1;
                }

                var pubKeyContent = (await File.ReadAllTextAsync(pubKeyPath, ct)).Trim();

                SshSignedCertResponse? result = null;
                await AnsiConsole.Status().StartAsync("Signing SSH key...", async _ =>
                {
                    result = await client.Api.V1.Projects[projectSlug].Environments[envSlug].Ssh.Sign.PostAsync(
                        new SshSignRequest { PublicKey = pubKeyContent, RoleName = roleName },
                        cancellationToken: ct);
                });

                if (result?.SignedKey is null)
                {
                    output.WriteError("No signed certificate returned.");
                    return 1;
                }

                await File.WriteAllTextAsync(sshCertPath, result.SignedKey + "\n", ct);
                // Also write to the sign-convention path for compatibility with `bella ssh sign`
                if (sshCertPath != signCertPath)
                    await File.WriteAllTextAsync(signCertPath, result.SignedKey + "\n", ct);
                AnsiConsole.MarkupLine($"[green]✓[/] Certificate written to [bold]{sshCertPath}[/]");
            }

            // Spawn SSH — OpenSSH auto-discovers {privKeyPath}-cert.pub alongside the identity
            AnsiConsole.MarkupLine($"\n[cyan]Connecting to {settings.Host}...[/]\n");

            var psi = new ProcessStartInfo("ssh") { UseShellExecute = false };
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(privKeyPath);
            psi.ArgumentList.Add(settings.Host);
            var proc = Process.Start(psi);
            if (proc is null)
            {
                output.WriteError("Failed to start ssh process. Is 'ssh' installed and on your PATH?");
                return 1;
            }

            await proc.WaitForExitAsync(ct);
            return proc.ExitCode;
        }
        catch (InvalidOperationException ex)
        {
            output.WriteError(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to connect: {ex.Message}");
            return 1;
        }
    }
}
