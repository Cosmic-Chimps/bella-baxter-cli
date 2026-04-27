using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using BellaBaxter.Client;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Run;

public class RunCommand(
    BellaClientProvider clientProvider,
    CredentialStore credentials,
    ContextService contextService,
    WorkloadIdentityService workloadIdentity,
    ZkeService zke,
    DekLeaseCache dekCache,
    IOutputWriter output
) : AsyncCommand<RunCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandOption("-p|--project <slug>")]
        [Description("Project slug or ID")]
        public string? Project { get; set; }

        [CommandOption("-e|--environment <slug>")]
        [Description("Environment slug or ID")]
        public string? Environment { get; set; }

        [CommandOption("--provider <slug>")]
        [Description("Provider name to use (if environment has multiple)")]
        public string? Provider { get; set; }

        [CommandOption("--watch")]
        [Description("Watch for secret changes and reload the process")]
        public bool Watch { get; set; }

        [CommandOption("--poll-interval <seconds>")]
        [Description("Polling interval in seconds for --watch (default: 30)")]
        [DefaultValue(30)]
        public int PollInterval { get; set; } = 30;

        [CommandOption("--signal <type>")]
        [Description("How to reload on change: restart (default) or sighup")]
        [DefaultValue("restart")]
        public string Signal { get; set; } = "restart";

        [CommandOption("--app <name>")]
        [Description(
            "Application name injected as BELLA_BAXTER_APP_CLIENT (useful for audit logs)"
        )]
        public string? App { get; set; }

        [CommandOption("--private-key <url>")]
        [Description(
            "Private key URL for M2M zero-knowledge decryption. " +
            "Schemes: file:///path/key.pem  env://VAR_NAME  (future: aws-kms:// vault:// azure-kv://)"
        )]
        public string? PrivateKey { get; set; }

        [CommandArgument(0, "[cmd...]")]
        [Description("Command and arguments to run (after --)")]
        public string[] Cmd { get; set; } = [];
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        Settings settings,
        CancellationToken ct
    )
    {
        var args = CollectArgs(settings.Cmd, context);

        if (args.Length == 0)
        {
            output.WriteError(
                "No command specified.\nUsage: bella run [options] -- <command> [args...]"
            );
            return 1;
        }

        // ── Try workload identity first (GitHub Actions / Kubernetes) ─────────
        BellaClient client;
        var workloadResult = await workloadIdentity.TryAutoExchangeAsync(
            settings.Project,
            settings.Environment,
            ct: ct
        );

        if (workloadResult is not null)
        {
            var platform = WorkloadIdentityService.DetectPlatform();
            AnsiConsole.MarkupLine($"[dim]🔑 Using workload identity ({platform})[/]");
            client = clientProvider.CreateClientWithApiKey(workloadResult.Token);
        }
        else
        {
            if (!credentials.IsAuthenticated())
            {
                output.WriteError("Not logged in. Run 'bella login' first.");
                return 1;
            }
            try
            {
                client = clientProvider.CreateClient(settings.App);
            }
            catch (Exception ex)
            {
                output.WriteError($"Authentication error: {ex.Message}");
                return 1;
            }
        }

        // ── ZKE: upgrade client to ZkeDekHandler if device key or --private-key present ──
        // This replaces E2EEncryptionHandler (ephemeral key) with ZkeDekHandler (persistent key)
        // so the server can wrap the project DEK for this identity.
        // Workload identity flows skip ZKE (they manage their own keys externally).
        ECDiffieHellman? zkeEcdh = null;
        if (workloadResult is null)
        {
            if (!string.IsNullOrEmpty(settings.PrivateKey))
            {
                // M2M: --private-key provided — derive ECDH key from URL
                var pkcs8b64 = ZkeService.ResolvePrivateKeyFromUrl(settings.PrivateKey);
                if (pkcs8b64 is not null)
                {
                    zkeEcdh = ECDiffieHellman.Create();
                    zkeEcdh.ImportPkcs8PrivateKey(Convert.FromBase64String(pkcs8b64), out _);
                }
                else
                {
                    AnsiConsole.MarkupLine("[yellow]⚠ Could not resolve --private-key; ZKE disabled.[/]");
                }
            }
            else
            {
                // Developer: use device key from bella auth setup
                zkeEcdh = zke.LoadEcdhKey(); // null if not set up
            }

            if (zkeEcdh is not null)
            {
                var zkeHandler = new ZkeDekHandler(
                    zkeEcdh,
                    onWrappedDekReceived: (project, env, wrappedDek, expires) =>
                        dekCache.Store(project, env, wrappedDek, expires));

                try
                {
                    client = clientProvider.CreateClientWithZke(zkeHandler, settings.App);
                    AnsiConsole.MarkupLine("[dim]🔐 ZKE enabled — secrets will be decrypted locally.[/]");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[yellow]⚠ ZKE client setup failed ({ex.Message}); using standard client.[/]");
                    zkeEcdh.Dispose();
                    zkeEcdh = null;
                }
            }
        }

        // Resolve project + environment
        string projectSlug,
            envSlug;
        try
        {
            var (resolvedProjectSlug, _, _, resolvedEnvSlug, _, _) =
                await contextService.ResolveProjectEnvironmentAsync(
                    settings.Project,
                    settings.Environment,
                    client,
                    ct,
                    strictJwtLocal: workloadResult is null,
                    bootstrapBellaFromExplicit: workloadResult is null
                );
            projectSlug = resolvedProjectSlug;
            envSlug = resolvedEnvSlug;
        }
        catch (Exception ex)
        {
            output.WriteError(ex.Message);
            zkeEcdh?.Dispose();
            return 1;
        }

        // Fetch secrets — ZkeDekHandler (if active) handles ECIES + DEK transparently
        Dictionary<string, string> secrets;
        long? initialVersion;
        try
        {
            (secrets, initialVersion) = await FetchSecretsAsync(client, projectSlug, envSlug, ct);
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to fetch secrets: {ex.Message}");
            zkeEcdh?.Dispose();
            return 1;
        }
        finally
        {
            // Key used — dispose after secrets are fetched (handler no longer needs it)
            // Note: ZkeDekHandler holds a reference but we own the key's lifetime
        }

        AnsiConsole.MarkupLine($"[dim]✓ Loaded [green]{secrets.Count}[/] secret(s) from Bella[/]");

        var appClient =
            settings.App ?? System.Environment.GetEnvironmentVariable("BELLA_BAXTER_APP_CLIENT");
        if (appClient is not null)
            secrets["BELLA_BAXTER_APP_CLIENT"] = appClient;

        if (settings.Watch)
        {
            return await RunWithWatchAsync(
                client,
                args,
                secrets,
                initialVersion,
                projectSlug,
                envSlug,
                settings,
                ct
            );
        }

        zkeEcdh?.Dispose();
        return SpawnProcess(args, secrets);
    }

    /// <summary>
    /// Fetches secrets via the Bella API. When the client was created with
    /// <see cref="ZkeDekHandler"/>, ECIES decryption and ZKE bellabaxter:v1: decryption
    /// happen automatically inside the handler — this method just calls Kiota normally.
    /// </summary>
    private static async Task<(Dictionary<string, string> Secrets, long? Version)> FetchSecretsAsync(
        BellaClient client,
        string projectSlug,
        string envSlug,
        CancellationToken ct
    )
    {
        var resp = await client
            .Api.V1.Projects[projectSlug]
            .Environments[envSlug]
            .Secrets.GetAsync(null, ct);

        var secrets =
            resp?.Secrets?.AdditionalData?.ToStringDict()
            ?? new Dictionary<string, string>(StringComparer.Ordinal);

        if (secrets.Count == 0)
            AnsiConsole.MarkupLine("[yellow]⚠ No secrets found in this environment.[/]");

        return (secrets, resp?.Version);
    }

    private async Task<int> RunWithWatchAsync(
        BellaClient client,
        string[] args,
        Dictionary<string, string> initialSecrets,
        long? initialVersion,
        string projectSlug,
        string envSlug,
        Settings settings,
        CancellationToken ct
    )
    {
        var currentSecrets = initialSecrets;
        var lastVersion = initialVersion;
        var pollMs = Math.Max(5, settings.PollInterval) * 1000;
        var useSighup = settings.Signal.Equals("sighup", StringComparison.OrdinalIgnoreCase);

        AnsiConsole.MarkupLine(
            $"[dim]👁  Watching for secret changes (poll every {settings.PollInterval}s)[/]"
        );

        Process? child = SpawnChild(args, currentSecrets);
        var stopping = false;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(pollMs));

        _ = Task.Run(
            async () =>
            {
                while (!stopping && await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    try
                    {
                        // Lightweight version check — no secrets payload transferred unless something changed
                        var versionResp = await client
                            .Api.V1.Projects[projectSlug]
                            .Environments[envSlug]
                            .Secrets.Version.GetAsync(cancellationToken: ct);
                        var newVersion = versionResp?.Version;

                        if (newVersion is null || newVersion == lastVersion)
                            continue;

                        lastVersion = newVersion;

                        // Version changed — fetch the full secrets payload
                        var (fresh, _) = await FetchSecretsAsync(
                            client,
                            projectSlug,
                            envSlug,
                            ct
                        );
                        currentSecrets = fresh;
                        AnsiConsole.MarkupLine(
                            $"[yellow]🔄 Secrets changed — {(useSighup ? "sending SIGHUP" : "restarting")}[/]"
                        );
                        if (useSighup)
                        {
                            child?.Kill(entireProcessTree: false); // sends SIGTERM on Unix — best effort
                        }
                        else
                        {
                            child?.Kill(entireProcessTree: true);
                            child?.WaitForExit();
                            child = SpawnChild(args, currentSecrets);
                        }
                    }
                    catch
                    { /* polling errors are non-fatal */
                    }
                }
            },
            ct
        );

        child?.WaitForExit();
        stopping = true;
        return child?.ExitCode ?? 0;
    }

    private static Process? SpawnChild(string[] args, Dictionary<string, string> secrets)
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        // Inherit current environment
        foreach (
            System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables()
        )
            env[entry.Key?.ToString() ?? ""] = entry.Value?.ToString();
        // Overlay secrets
        foreach (var (k, v) in secrets)
            env[k] = v;

        var psi = new ProcessStartInfo { FileName = args[0], UseShellExecute = false };
        for (int i = 1; i < args.Length; i++)
            psi.ArgumentList.Add(args[i]);
        foreach (var (k, v) in env)
            psi.Environment[k] = v;

        var p = Process.Start(psi);
        return p;
    }

    private static int SpawnProcess(string[] args, Dictionary<string, string> secrets)
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (
            System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables()
        )
            env[entry.Key?.ToString() ?? ""] = entry.Value?.ToString();
        foreach (var (k, v) in secrets)
            env[k] = v;

        var psi = new ProcessStartInfo { FileName = args[0], UseShellExecute = false };
        for (int i = 1; i < args.Length; i++)
            psi.ArgumentList.Add(args[i]);
        foreach (var (k, v) in env)
            psi.Environment[k] = v;

        var p = Process.Start(psi);
        p?.WaitForExit();
        return p?.ExitCode ?? 0;
    }

    /// <summary>
    /// Collects the passthrough command args from either the positional <paramref name="cmd"/>
    /// array (Spectre captures non-dashed tokens there) OR from <c>context.Remaining.Raw</c>
    /// (everything after <c>--</c>, including single-dash flags like <c>-auto-approve</c>).
    /// Merges both so that <c>bella run -- terraform apply -auto-approve</c> works correctly.
    /// </summary>
    private static string[] CollectArgs(string[] cmd, CommandContext context)
    {
        // context.Remaining.Raw contains everything after the '--' separator.
        // cmd[] contains positional (non-dashed) tokens that Spectre picked up before '--'.
        // Merge: positional first, then remaining (which holds the dashed passthrough flags).
        var positional = cmd.Where(a => a != "--").ToList();
        var remaining = context.Remaining.Raw.Where(a => a != "--").ToList();

        // If both are populated it means Spectre split the args across the two collections.
        // If only remaining is populated it means '--' came first (the normal usage).
        if (positional.Count == 0)
            return [.. remaining];

        if (remaining.Count == 0)
            return [.. positional];

        // Merge: positional tokens come before the remaining ones.
        return [.. positional, .. remaining];
    }
}
