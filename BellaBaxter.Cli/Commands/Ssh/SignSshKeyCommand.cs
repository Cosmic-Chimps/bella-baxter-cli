using BellaBaxter.Client;
using BellaBaxter.Client.Models;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Ssh;

public class SignSshKeySettings : CommandSettings
{
    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("-e|--environment <SLUG>")]
    public string? Environment { get; init; }

    [CommandOption("-r|--role <ROLE>")]
    public string? Role { get; init; }

    [CommandOption("-k|--key <PATH>")]
    [System.ComponentModel.Description("Path to SSH public key file (default: ~/.ssh/id_ed25519.pub or id_rsa.pub)")]
    public string? KeyPath { get; init; }

    [CommandOption("-t|--ttl <TTL>")]
    [System.ComponentModel.Description("Certificate TTL (e.g. 1h)")]
    public string? Ttl { get; init; }

    [CommandOption("--principals <PRINCIPALS>")]
    [System.ComponentModel.Description("Comma-separated valid principals")]
    public string? Principals { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class SignSshKeyCommand(BellaClientProvider provider, ContextService context, IOutputWriter output)
    : AsyncCommand<SignSshKeySettings>
{
    public override async Task<int> ExecuteAsync(CommandContext ctx, SignSshKeySettings settings, CancellationToken ct)
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
                    if (Console.IsOutputRedirected || output is JsonOutputWriter)
                    {
                        output.WriteError("--role is required in non-interactive mode.");
                        return 1;
                    }
                    roleName = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("[bold]Select SSH role:[/]")
                            .AddChoices(roleList.Select(r => r.Name ?? "")));
                }
            }

            // Resolve public key path
            var pubKeyPath = ResolvePublicKeyPath(settings.KeyPath);
            if (pubKeyPath is null)
            {
                output.WriteError("No SSH public key found. Pass --key <path>.");
                return 1;
            }

            var pubKeyContent = (await File.ReadAllTextAsync(pubKeyPath, ct)).Trim();
            var certFilePath = ToCertPath(pubKeyPath);

            SshSignedCertResponse? result = null;
            await AnsiConsole.Status().StartAsync("Signing SSH key...", async _ =>
            {
                result = await client.Api.V1.Projects[projectSlug].Environments[envSlug].Ssh.Sign.PostAsync(
                    new SshSignRequest
                    {
                        PublicKey = pubKeyContent,
                        RoleName = roleName,
                        Ttl = settings.Ttl,
                        ValidPrincipals = settings.Principals
                    },
                    cancellationToken: ct);
            });

            if (result?.SignedKey is null)
            {
                output.WriteError("No signed certificate returned.");
                return 1;
            }

            await File.WriteAllTextAsync(certFilePath, result.SignedKey + "\n", ct);

            AnsiConsole.MarkupLine($"[green]✓[/] Certificate written to [bold]{certFilePath}[/]");
            AnsiConsole.MarkupLine($"[dim]  Serial: {result.SerialNumber}[/]");

            if (!string.IsNullOrWhiteSpace(result.Instructions))
            {
                AnsiConsole.MarkupLine($"\n[dim]{Markup.Escape(result.Instructions)}[/]");
            }

            var privKeyPath = pubKeyPath.EndsWith(".pub") ? pubKeyPath[..^4] : pubKeyPath;
            AnsiConsole.MarkupLine("\n[bold]To use this certificate:[/]");
            AnsiConsole.MarkupLine($"[cyan]  ssh -i {privKeyPath} -o CertificateFile={certFilePath} user@host[/]");

            return 0;
        }
        catch (InvalidOperationException ex)
        {
            output.WriteError(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to sign SSH key: {ex.Message}");
            return 1;
        }
    }

    internal static string? ResolvePublicKeyPath(string? explicit_)
    {
        if (!string.IsNullOrWhiteSpace(explicit_)) return explicit_;
        var sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        // Check common key types in preference order
        foreach (var name in new[] { "id_ed25519.pub", "id_ecdsa.pub", "id_rsa.pub", "id_dsa.pub" })
        {
            var path = Path.Combine(sshDir, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>
    /// Given a private key path (which the user passed via --key), derive the corresponding
    /// public key path. Handles non-standard naming like vee-stage.private → vee-stage.pub.
    /// </summary>
    internal static string DerivePublicKeyPath(string privKeyPath)
    {
        // Standard: id_ed25519 → id_ed25519.pub
        var standard = privKeyPath + ".pub";
        if (File.Exists(standard)) return standard;

        // Non-standard: vee-stage.private → vee-stage.pub
        if (privKeyPath.EndsWith(".private", StringComparison.OrdinalIgnoreCase))
        {
            var stem = privKeyPath[..^".private".Length];
            var stemPub = stem + ".pub";
            if (File.Exists(stemPub)) return stemPub;
        }

        // Return standard path so error message is helpful
        return standard;
    }

    /// <summary>
    /// Resolve private key path — also tries appending .private if the path doesn't exist.
    /// </summary>
    internal static string ResolvePrivateKeyPath(string keyPath)
    {
        if (File.Exists(keyPath)) return keyPath;
        var withPrivate = keyPath + ".private";
        if (File.Exists(withPrivate)) return withPrivate;
        return keyPath; // return as-is; SSH will emit a clear error
    }

    internal static string ToCertPath(string pubKeyPath) =>
        pubKeyPath.EndsWith(".pub") ? pubKeyPath[..^4] + "-cert.pub" : pubKeyPath + "-cert.pub";
}
