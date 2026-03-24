using BellaBaxter.Client;
using BellaBaxter.Client.Models;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using System.Diagnostics;
using System.Text.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BellaCli.Commands.Agent;

// ── Config model ─────────────────────────────────────────────────────────────

public class AgentConfig
{
    public List<WatchConfig> Watches { get; set; } = [];
    public List<SinkConfig> Sinks { get; set; } = [];
    public ProcessConfig Process { get; set; } = new();
}

public class WatchConfig
{
    public string Project { get; set; } = "";
    public string Environment { get; set; } = "";
    public string? Provider { get; set; }
    [YamlMember(Alias = "poll-interval")]
    public int PollInterval { get; set; } = 30;
}

public class SinkConfig
{
    public string Type { get; set; } = "dotenv"; // dotenv | json | yaml
    public string Path { get; set; } = ".env";
}

public class ProcessConfig
{
    public string Signal { get; set; } = "none"; // sighup | sigterm | none
    [YamlMember(Alias = "pid-file")]
    public string? PidFile { get; set; }
}

// ── Command ──────────────────────────────────────────────────────────────────

public class AgentSettings : CommandSettings
{
    [CommandOption("-c|--config <PATH>")]
    [System.ComponentModel.Description("Path to agent config file (default: bella-agent.yaml)")]
    public string Config { get; init; } = "bella-agent.yaml";

    [CommandOption("--init")]
    [System.ComponentModel.Description("Scaffold a starter bella-agent.yaml in the current directory")]
    public bool Init { get; init; }
}

public class AgentCommand(BellaClientProvider provider, IOutputWriter output)
    : AsyncCommand<AgentSettings>
{
    private const string StarterConfig = """
        # bella-agent.yaml
        # Bella Agent sidecar configuration
        #
        # Run: bella agent --config bella-agent.yaml

        watches:
          - project: my-project          # project name or slug
            environment: production      # environment name or slug
            # provider: aws-prod         # optional — uses first provider if omitted
            poll-interval: 30            # seconds between hash checks

        sinks:
          - type: dotenv                 # dotenv | json | yaml
            path: ./.env
          # - type: json
          #   path: ./secrets.json

        process:
          signal: sighup                 # sighup | sigterm | none
          # pid-file: ./app.pid          # PID file to read — omit to skip signalling
        """;

    public override async Task<int> ExecuteAsync(CommandContext ctx, AgentSettings settings, CancellationToken ct)
    {
        if (settings.Init)
            return ScaffoldConfig();

        BellaClient client;
        try { client = provider.CreateClient(); }
        catch (InvalidOperationException)
        {
            output.WriteError("Not logged in. Run 'bella login' first.");
            return 1;
        }

        var configPath = Path.GetFullPath(settings.Config);
        if (!File.Exists(configPath))
        {
            output.WriteError($"Config file not found: {configPath}");
            output.WriteInfo("Run 'bella agent --init' to create a starter config.");
            return 1;
        }

        AgentConfig config;
        try { config = LoadConfig(configPath); }
        catch (Exception ex)
        {
            output.WriteError($"Config error: {ex.Message}");
            return 1;
        }

        AnsiConsole.MarkupLine($"[cyan]🤖 bella agent[/] [dim]— {config.Watches.Count} watch(es), {config.Sinks.Count} sink(s), signal={config.Process.Signal}[/]");

        // Resolve all watches to project/env/provider slugs
        var resolvedWatches = new List<ResolvedWatch>();
        foreach (var watch in config.Watches)
        {
            try
            {
                var resolved = await ResolveWatchAsync(watch, client, ct);
                resolvedWatches.Add(resolved);
                AnsiConsole.MarkupLine($"[dim]  ✓ Watching [bold]{watch.Project}[/]/[bold]{watch.Environment}[/] (poll every {watch.PollInterval}s)[/]");
            }
            catch (Exception ex)
            {
                output.WriteError($"Could not resolve watch {watch.Project}/{watch.Environment}: {ex.Message}");
                return 1;
            }
        }

        // Initial fetch — write all sinks on startup regardless of hash
        AnsiConsole.MarkupLine("[dim]  Fetching initial secrets...[/]");
        var initialSecrets = await FetchAllSecretsAsync(resolvedWatches, client, ct);
        WriteAllSinks(config.Sinks, initialSecrets);
        AnsiConsole.MarkupLine($"[green]✓[/] Wrote [bold]{initialSecrets.Count}[/] secret(s) to [bold]{config.Sinks.Count}[/] sink(s)");
        AnsiConsole.MarkupLine("[cyan]  Watching for secret changes... (Ctrl+C to stop)[/]\n");

        // Start per-watch polling using PeriodicTimer
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var pollTasks = resolvedWatches.Select(rw => PollWatchAsync(rw, config, resolvedWatches, client, cts.Token)).ToArray();

        try { await Task.WhenAll(pollTasks); }
        catch (OperationCanceledException) { }

        AnsiConsole.MarkupLine("[dim]Agent stopped.[/]");
        return 0;
    }

    private async Task PollWatchAsync(
        ResolvedWatch watch,
        AgentConfig config,
        List<ResolvedWatch> allWatches,
        BellaClient client,
        CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, watch.Source.PollInterval));
        using var timer = new PeriodicTimer(interval);

        string? lastHash = null;

        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                var hashResp = await client.Api.V1.Projects[watch.ProjectSlug]
                    .Environments[watch.EnvSlug]
                    .Providers[watch.ProviderSlug]
                    .Secrets.Hash.GetAsync(cancellationToken: ct);

                var newHash = hashResp?.Hash;
                if (newHash is null || newHash == lastHash) continue;

                lastHash = newHash;
                AnsiConsole.MarkupLine($"[yellow]  🔄 {watch.Source.Project}/{watch.Source.Environment} changed (hash: {newHash[..Math.Min(18, newHash.Length)]}...)[/]");

                var fresh = await FetchAllSecretsAsync(allWatches, client, ct);
                WriteAllSinks(config.Sinks, fresh);
                AnsiConsole.MarkupLine($"[green]  ✓[/] Updated [bold]{config.Sinks.Count}[/] sink(s) with [bold]{fresh.Count}[/] secret(s)");

                SignalProcess(config.Process);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  ⚠ Poll error ({watch.Source.Project}/{watch.Source.Environment}): {ex.Message}");
            }
        }
    }

    private static async Task<Dictionary<string, string>> FetchAllSecretsAsync(
        List<ResolvedWatch> watches,
        BellaClient client,
        CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var watch in watches)
        {
            try
            {
                var resp = await client.Api.V1.Projects[watch.ProjectSlug]
                    .Environments[watch.EnvSlug]
                    .Providers[watch.ProviderSlug]
                    .Secrets.GetAsync(cancellationToken: ct);

                if (resp?.AdditionalData?.TryGetValue("secrets", out var rawSecrets) == true && rawSecrets is not null)
                {
                    var json = JsonSerializer.Serialize(rawSecrets);
                    var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (parsed is not null)
                        foreach (var kvp in parsed)
                            result[kvp.Key] = kvp.Value;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  ⚠ Could not fetch from {watch.Source.Project}/{watch.Source.Environment}: {ex.Message}");
            }
        }
        return result;
    }

    private static async Task<ResolvedWatch> ResolveWatchAsync(WatchConfig watch, BellaClient client, CancellationToken ct)
    {
        // Resolve provider for this environment
        var providers = await client.Api.V1.Projects[watch.Project].Environments[watch.Environment].Providers.GetAsync(cancellationToken: ct) ?? [];

        var providerSlug = watch.Provider is not null
            ? (providers.FirstOrDefault(p =>
                string.Equals(p.ProviderName, watch.Provider, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.ProviderId, watch.Provider, StringComparison.OrdinalIgnoreCase))
                ?.ProviderName ?? watch.Provider)
            : (providers.FirstOrDefault()?.ProviderName ?? "");

        if (string.IsNullOrWhiteSpace(providerSlug))
            throw new InvalidOperationException($"No providers assigned to environment '{watch.Environment}' in project '{watch.Project}'.");

        return new ResolvedWatch(watch.Project, watch.Environment, providerSlug, watch);
    }

    // ── Sink writers ─────────────────────────────────────────────────────────

    private static void WriteAllSinks(List<SinkConfig> sinks, Dictionary<string, string> secrets)
    {
        foreach (var sink in sinks)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(sink.Path)) ?? ".");
                switch (sink.Type.ToLowerInvariant())
                {
                    case "dotenv":
                        var dotenvLines = secrets.OrderBy(k => k.Key).Select(kvp => $"{kvp.Key}={JsonSerializer.Serialize(kvp.Value)}");
                        File.WriteAllText(sink.Path, string.Join('\n', dotenvLines) + '\n');
                        break;
                    case "json":
                        var ordered = new SortedDictionary<string, string>(secrets);
                        File.WriteAllText(sink.Path, JsonSerializer.Serialize(ordered, new JsonSerializerOptions { WriteIndented = true }) + '\n');
                        break;
                    case "yaml":
                        var yamlLines = secrets.OrderBy(k => k.Key).Select(kvp => $"{kvp.Key}: {JsonSerializer.Serialize(kvp.Value)}");
                        File.WriteAllText(sink.Path, string.Join('\n', yamlLines) + '\n');
                        break;
                    default:
                        Console.Error.WriteLine($"  ⚠ Unknown sink type '{sink.Type}' — skipping");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  ⚠ Failed to write sink '{sink.Path}': {ex.Message}");
            }
        }
    }

    // ── Process signalling ────────────────────────────────────────────────────

    private static void SignalProcess(ProcessConfig proc)
    {
        if (proc.Signal == "none" || string.IsNullOrWhiteSpace(proc.PidFile)) return;

        int pid;
        try
        {
            var raw = File.ReadAllText(proc.PidFile).Trim();
            if (!int.TryParse(raw, out pid)) { Console.Error.WriteLine($"  ⚠ Could not parse PID from {proc.PidFile}"); return; }
        }
        catch { Console.Error.WriteLine($"  ⚠ Could not read PID file {proc.PidFile}"); return; }

        try
        {
            var process = Process.GetProcessById(pid);
            // On Unix: send SIGHUP (1) or SIGTERM (15) via kill
            var signal = proc.Signal.ToLowerInvariant() == "sighup" ? "HUP" : "TERM";
            if (OperatingSystem.IsWindows())
            {
                if (proc.Signal.ToLowerInvariant() == "sigterm")
                    process.Kill();
            }
            else
            {
                Process.Start("kill", $"-s {signal} {pid}")?.WaitForExit();
            }
            AnsiConsole.MarkupLine($"[dim]  ↩ Sent {proc.Signal.ToUpperInvariant()} to PID {pid}[/]");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  ⚠ Could not signal PID {pid}: {ex.Message}");
        }
    }

    // ── Config loading ────────────────────────────────────────────────────────

    private static AgentConfig LoadConfig(string path)
    {
        var yaml = File.ReadAllText(path);
        // Expand ${ENV_VAR} references
        yaml = System.Text.RegularExpressions.Regex.Replace(yaml, @"\$\{([^}]+)\}", m =>
            Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? "");

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(HyphenatedNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var config = deserializer.Deserialize<AgentConfig>(yaml);
        if (config.Watches.Count == 0)
            throw new InvalidOperationException("Config must have at least one entry under 'watches:'");
        if (config.Sinks.Count == 0)
            throw new InvalidOperationException("Config must have at least one entry under 'sinks:'");

        return config;
    }

    private static int ScaffoldConfig()
    {
        var dest = Path.GetFullPath("bella-agent.yaml");
        if (File.Exists(dest))
        {
            Console.Error.WriteLine($"⚠ {dest} already exists — not overwriting.");
            return 1;
        }
        File.WriteAllText(dest, StarterConfig + "\n");
        AnsiConsole.MarkupLine($"[green]✓[/] Created [bold]{dest}[/]");
        AnsiConsole.MarkupLine("[dim]  Edit the file, then run: bella agent[/]");
        return 0;
    }
}

// ── Resolved watch (project + env + provider slugs) ──────────────────────────

internal record ResolvedWatch(string ProjectSlug, string EnvSlug, string ProviderSlug, WatchConfig Source);
