using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Channels;
using BellaBaxter.Client;
using BellaCli.Commands;
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

        [CommandOption("-e|--env|--environment <slug>")]
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
            "Private key URL for M2M zero-knowledge decryption. "
                + "Schemes: file:///path/key.pem  env://VAR_NAME  (future: aws-kms:// vault:// azure-kv://)"
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
                    AnsiConsole.MarkupLine(
                        "[yellow]⚠ Could not resolve --private-key; ZKE disabled.[/]"
                    );
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
                        dekCache.Store(project, env, wrappedDek, expires)
                );

                try
                {
                    client = clientProvider.CreateClientWithZke(zkeHandler, settings.App);
                    AnsiConsole.MarkupLine(
                        "[dim]🔐 ZKE enabled — secrets will be decrypted locally.[/]"
                    );
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine(
                        $"[yellow]⚠ ZKE client setup failed ({ex.Message}); using standard client.[/]"
                    );
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
    private static async Task<(
        Dictionary<string, string> Secrets,
        long? Version
    )> FetchSecretsAsync(
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

        // Seed lastVersion from the /secrets/version endpoint so the initial value uses the same
        // unit (Ticks) as every subsequent poll.  The /secrets full-fetch response Version field
        // uses ToUnixTimeSeconds() — a completely different magnitude — which would cause the very
        // first poll to always report a "version change" even when nothing was modified.
        long? lastVersion;
        try
        {
            var seedResp = await client
                .Api.V1.Projects[projectSlug]
                .Environments[envSlug]
                .Secrets.Version.GetAsync(cancellationToken: ct);
            lastVersion = seedResp?.Version ?? initialVersion;
        }
        catch
        {
            // If the version endpoint is unavailable at startup fall back to the value from the
            // full secrets fetch.  The first poll may fire a spurious restart but subsequent ones
            // will be correct once lastVersion is updated to a Ticks value.
            lastVersion = initialVersion;
        }

        var pollMs = Math.Max(5, settings.PollInterval) * 1000;
        var useSighup = settings.Signal.Equals("sighup", StringComparison.OrdinalIgnoreCase);

        AnsiConsole.MarkupLine(
            $"[dim]👁  Watching for secret changes (poll every {settings.PollInterval}s)[/]"
        );

        // restartCh delivers fresh secrets from the polling task to the main loop.
        // Capacity 1 + DropOldest: only the latest pending restart ever matters.
        var restartCh = Channel.CreateBounded<Dictionary<string, string>>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest }
        );

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(pollMs));

        var debug = !string.IsNullOrEmpty(
            System.Environment.GetEnvironmentVariable("BELLA_BAXTER_DEBUG")
        );
        void Debug(string msg)
        {
            if (debug)
                AnsiConsole.MarkupLine($"[dim grey]🐛 {msg}[/]");
        }

        // Polling task — detects version changes and signals via channel only;
        // never touches the child process directly.
        _ = Task.Run(
            async () =>
            {
                while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    try
                    {
                        // Lightweight version check — no secrets payload transferred unless something changed
                        var versionResp = await client
                            .Api.V1.Projects[projectSlug]
                            .Environments[envSlug]
                            .Secrets.Version.GetAsync(cancellationToken: ct);
                        var newVersion = versionResp?.Version;

                        Debug($"poll: lastVersion={lastVersion} newVersion={newVersion}");

                        if (newVersion is null || newVersion == lastVersion)
                            continue;

                        Debug(
                            $"poll: version changed {lastVersion} → {newVersion}, fetching secrets…"
                        );

                        // Version changed — fetch the full secrets payload.
                        // IMPORTANT: update lastVersion only AFTER a successful fetch so that
                        // a transient fetch failure does not permanently consume the version
                        // bump (which would cause the change to be silently skipped forever).
                        var (fresh, _) = await FetchSecretsAsync(client, projectSlug, envSlug, ct);

                        // Commit the version only now that we have the new secrets.
                        lastVersion = newVersion;

                        AnsiConsole.MarkupLine(
                            $"[yellow]🔄 Secrets changed — {(useSighup ? "sending SIGHUP" : "restarting")}[/]"
                        );

                        Debug(
                            $"poll: queuing restart (pid={restartCh.Reader.Count} items already pending)"
                        );

                        // Signal the main loop to restart with the new secrets.
                        restartCh.Writer.TryWrite(fresh);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        Debug(
                            $"poll: error (non-fatal) — {ex.GetType().Name}: {Markup.Escape(ex.Message)}"
                        );
                    }
                }
            },
            ct
        );

        Process? child = SpawnChild(args, currentSecrets);
        Debug($"watch: initial child spawned PID={child?.Id}");

        // Main loop: wait for the child to exit OR for a restart signal.
        // All process management happens here — no shared state with the polling task.
        while (!ct.IsCancellationRequested)
        {
            var processExitTask = child?.WaitForExitAsync(ct) ?? Task.CompletedTask;
            var restartWaitTask = restartCh.Reader.WaitToReadAsync(ct).AsTask();

            Task winner;
            try
            {
                winner = await Task.WhenAny(processExitTask, restartWaitTask);
            }
            catch (OperationCanceledException)
            {
                Debug("watch: cancelled (Ctrl+C), killing child");
                if (child is not null)
                    await KillAndWaitAsync(child, sighupMode: false);
                break;
            }

            if (ct.IsCancellationRequested)
            {
                Debug("watch: ct cancelled, killing child");
                if (child is not null)
                    await KillAndWaitAsync(child, sighupMode: false);
                break;
            }

            if (winner == restartWaitTask && restartCh.Reader.TryRead(out var newSecrets))
            {
                Debug($"watch: restart signal received, killing PID={child?.Id}");
                // Restart the child with the new secrets.
                var old = child;
                child = SpawnChild(args, newSecrets);
                Debug($"watch: new child spawned PID={child?.Id}");
                if (old is not null)
                    await KillAndWaitAsync(old, useSighup);
                // Loop — next iteration waits for the new child.
            }
            else
            {
                // processExitTask fired: child exited naturally (or both fired simultaneously).
                // Check whether a restart arrived at the same time.
                Debug(
                    $"watch: child PID={child?.Id} exited (code={child?.ExitCode}), checking for pending restart"
                );
                if (restartCh.Reader.TryRead(out var pendingSecrets))
                {
                    Debug("watch: pending restart found, spawning new child");
                    child = SpawnChild(args, pendingSecrets);
                    // Loop — wait for the freshly spawned child.
                }
                else
                {
                    Debug("watch: no pending restart — stopping watch");
                    break; // genuine natural exit
                }
            }
        }

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
        if (p is not null)
        {
            p.EnableRaisingEvents = true;
            // On Unix, move the child into its own process group immediately after fork.
            // This lets us kill(-pgid, SIGKILL) later, which catches every descendant
            // including processes that detach themselves (e.g. node workers / vite HMR
            // threads that call setsid internally). Kill(entireProcessTree:true) only
            // walks the parent-child chain and misses those detached processes.
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                UnixNative.TrySetProcessGroup(p.Id, p.Id);
        }
        return p;
    }

    /// <summary>
    /// Kills a process (and all of its descendants) then waits for it to exit.
    /// On Unix, kills the entire process group to catch detached sub-processes.
    /// </summary>
    private static async Task KillAndWaitAsync(Process process, bool sighupMode)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            int sig = sighupMode ? UnixNative.SIGTERM : UnixNative.SIGKILL;
            // Negative PID = kill every process in the process group whose PGID == |pid|.
            // We set PGID = child PID in SpawnChild, so this only hits our child's tree.
            if (UnixNative.Kill(-process.Id, sig) != 0)
            {
                // Group kill failed (e.g. process already gone) — fall back
                try
                {
                    process.Kill(entireProcessTree: !sighupMode);
                }
                catch { }
            }
        }
        else
        {
            try
            {
                process.Kill(entireProcessTree: !sighupMode);
            }
            catch { }
        }

        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch { }
    }

    private static class UnixNative
    {
        internal const int SIGKILL = 9;
        internal const int SIGTERM = 15;

        [DllImport("libc", EntryPoint = "setpgid", SetLastError = true)]
        private static extern int SetPgid(int pid, int pgid);

        [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
        internal static extern int Kill(int pid, int sig);

        internal static void TrySetProcessGroup(int pid, int pgid)
        {
            try
            {
                SetPgid(pid, pgid);
            }
            catch { }
        }
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
    /// Collects the passthrough command args by merging:
    /// 1. <c>PassthroughArgsHolder</c> — args stripped from the CLI args array in Program.cs
    ///    before Spectre.Console's parser runs (handles single-dash flags like -auto-approve).
    /// 2. Positional tokens in <paramref name="cmd"/> — non-dashed tokens Spectre captured.
    /// 3. <c>context.Remaining.Raw</c> — fallback for double-dash tokens after '--'.
    /// </summary>
    private static string[] CollectArgs(string[] cmd, CommandContext context)
    {
        // Primary: args stripped before Spectre saw them (handles -auto-approve etc.)
        var stripped = PassthroughArgsHolder.Get().Where(a => a != "--").ToList();
        if (stripped.Count > 0)
            return [.. stripped];

        // Fallback for double-dash passthrough tokens that Spectre did capture.
        var positional = cmd.Where(a => a != "--").ToList();
        var remaining = context.Remaining.Raw.Where(a => a != "--").ToList();

        if (positional.Count == 0)
            return [.. remaining];
        if (remaining.Count == 0)
            return [.. positional];
        return [.. positional, .. remaining];
    }
}
