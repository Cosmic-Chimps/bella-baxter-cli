using BellaCli.Infrastructure;
using BellaBaxter.Client;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Diagnostics;

namespace BellaCli.Commands.Run;

public class RunCommand(BellaClientProvider clientProvider, CredentialStore credentials, ContextService contextService, WorkloadIdentityService workloadIdentity, IOutputWriter output)
    : AsyncCommand<RunCommand.Settings>
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
        [Description("Application name injected as BELLA_BAXTER_APP_CLIENT (useful for audit logs)")]
        public string? App { get; set; }

        [CommandArgument(0, "[cmd...]")]
        [Description("Command and arguments to run (prefix with --)")]
        public string[] Cmd { get; set; } = [];
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var args = settings.Cmd.Where(a => a != "--").ToArray();

        // Remaining args after -- come from context.Remaining
        if (args.Length == 0 && context.Remaining.Raw.Count > 0)
            args = [.. context.Remaining.Raw];

        if (args.Length == 0)
        {
            output.WriteError("No command specified.\nUsage: bella run [options] -- <command> [args...]");
            return 1;
        }

        // ── Try workload identity first (GitHub Actions / Kubernetes) ─────────
        BellaClient client;
        var workloadResult = await workloadIdentity.TryAutoExchangeAsync(
            settings.Project, settings.Environment, ct: ct);

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
            try { client = clientProvider.CreateClient(settings.App); }
            catch (Exception ex) { output.WriteError($"Authentication error: {ex.Message}"); return 1; }
        }

        // Resolve project + environment
        string projectSlug, envSlug;
        try
        {
            (projectSlug, _, _) = await contextService.ResolveProjectAsync(settings.Project, client, ct);
            (envSlug, _, _) = await contextService.ResolveEnvironmentAsync(settings.Environment, projectSlug, client, ct);
        }
        catch (Exception ex)
        {
            output.WriteError(ex.Message);
            return 1;
        }

        // Fetch providers for environment
        Dictionary<string, string> secrets;
        long? initialVersion;
        try
        {
            (secrets, initialVersion) = await FetchSecretsAsync(client, projectSlug, envSlug, settings.Provider, ct);
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to fetch secrets: {ex.Message}");
            return 1;
        }

        AnsiConsole.MarkupLine($"[dim]✓ Loaded [green]{secrets.Count}[/] secret(s) from Bella[/]");

        var appClient = settings.App
                        ?? System.Environment.GetEnvironmentVariable("BELLA_BAXTER_APP_CLIENT");
        if (appClient is not null)
            secrets["BELLA_BAXTER_APP_CLIENT"] = appClient;

        if (settings.Watch)
        {
            return await RunWithWatchAsync(client, args, secrets, initialVersion, projectSlug, envSlug, settings, ct);
        }

        return SpawnProcess(args, secrets);
    }

    private async Task<(Dictionary<string, string> Secrets, long? Version)> FetchSecretsAsync(
        BellaClient client, string projectSlug, string envSlug, string? providerFilter, CancellationToken ct)
    {
        // Use the env-level endpoint — aggregates all providers, works with API keys,
        // and does not require the E2E encryption header needed by per-provider endpoints.
        var resp = await client.Api.V1.Projects[projectSlug].Environments[envSlug].Secrets.GetAsync(cancellationToken: ct);
        var secrets = resp?.Secrets?.AdditionalData?.ToStringDict()
                      ?? new Dictionary<string, string>(StringComparer.Ordinal);

        if (secrets.Count == 0)
            AnsiConsole.MarkupLine("[yellow]⚠ No secrets found in this environment.[/]");

        return (secrets, resp?.Version);
    }

    private async Task<int> RunWithWatchAsync(
        BellaClient client, string[] args, Dictionary<string, string> initialSecrets, long? initialVersion,
        string projectSlug, string envSlug, Settings settings, CancellationToken ct)
    {
        var currentSecrets = initialSecrets;
        var lastVersion = initialVersion;
        var pollMs = Math.Max(5, settings.PollInterval) * 1000;
        var useSighup = settings.Signal.Equals("sighup", StringComparison.OrdinalIgnoreCase);

        AnsiConsole.MarkupLine($"[dim]👁  Watching for secret changes (poll every {settings.PollInterval}s)[/]");

        Process? child = SpawnChild(args, currentSecrets);
        var stopping = false;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(pollMs));

        _ = Task.Run(async () =>
        {
            while (!stopping && await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    // Lightweight version check — no secrets payload transferred unless something changed
                    var versionResp = await client.Api.V1.Projects[projectSlug]
                        .Environments[envSlug].Secrets.Version.GetAsync(cancellationToken: ct);
                    var newVersion = versionResp?.Version;

                    if (newVersion is null || newVersion == lastVersion)
                        continue;

                    lastVersion = newVersion;

                    // Version changed — fetch the full secrets payload
                    var (fresh, _) = await FetchSecretsAsync(client, projectSlug, envSlug, settings.Provider, ct);
                    currentSecrets = fresh;
                    AnsiConsole.MarkupLine($"[yellow]🔄 Secrets changed — {(useSighup ? "sending SIGHUP" : "restarting")}[/]");
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
                catch { /* polling errors are non-fatal */ }
            }
        }, ct);

        child?.WaitForExit();
        stopping = true;
        return child?.ExitCode ?? 0;
    }

    private static Process? SpawnChild(string[] args, Dictionary<string, string> secrets)
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        // Inherit current environment
        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
            env[entry.Key?.ToString() ?? ""] = entry.Value?.ToString();
        // Overlay secrets
        foreach (var (k, v) in secrets)
            env[k] = v;

        var psi = new ProcessStartInfo
        {
            FileName = args[0],
            UseShellExecute = false,
        };
        for (int i = 1; i < args.Length; i++) psi.ArgumentList.Add(args[i]);
        foreach (var (k, v) in env) psi.Environment[k] = v;

        var p = Process.Start(psi);
        return p;
    }

    private static int SpawnProcess(string[] args, Dictionary<string, string> secrets)
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
            env[entry.Key?.ToString() ?? ""] = entry.Value?.ToString();
        foreach (var (k, v) in secrets)
            env[k] = v;

        var psi = new ProcessStartInfo
        {
            FileName = args[0],
            UseShellExecute = false,
        };
        for (int i = 1; i < args.Length; i++) psi.ArgumentList.Add(args[i]);
        foreach (var (k, v) in env) psi.Environment[k] = v;

        var p = Process.Start(psi);
        p?.WaitForExit();
        return p?.ExitCode ?? 0;
    }
}
