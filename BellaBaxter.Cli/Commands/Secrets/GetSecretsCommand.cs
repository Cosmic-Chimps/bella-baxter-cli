using System.Text.Json;
using System.Text.RegularExpressions;
using BellaBaxter.Client;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Secrets;

public class GetSecretsSettings : CommandSettings
{
    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("-e|--environment <SLUG>")]
    public string? Environment { get; init; }

    [CommandOption("--provider <SLUG>")]
    public string? Provider { get; init; }

    [CommandOption("-o|--output <FILE>")]
    public string? OutputFile { get; init; }

    /// <summary>
    /// Output format: env (default), json, or json-nested.
    /// When writing to a file (-o), format is auto-detected from the extension:
    ///   .json  → json-nested
    ///   .env   → env
    /// An explicit --format always overrides auto-detection.
    /// </summary>
    [CommandOption("--format <FORMAT>")]
    public string? Format { get; init; }

    /// <summary>Deprecated alias for --format json. Kept for backward compatibility.</summary>
    [CommandOption("--json")]
    public bool Json { get; init; }

    /// <summary>
    /// Application name sent as X-App-Client header for audit logs.
    /// Equivalent to setting BELLA_BAXTER_APP_CLIENT env var.
    /// </summary>
    [CommandOption("--app <NAME>")]
    public string? App { get; init; }
}

public class GetSecretsCommand(
    BellaClientProvider provider,
    ContextService context,
    IOutputWriter output
) : AsyncCommand<GetSecretsSettings>
{
    private static readonly Regex HierarchySeparator = new(@"__|:", RegexOptions.Compiled);

    public override async Task<int> ExecuteAsync(
        CommandContext ctx,
        GetSecretsSettings settings,
        CancellationToken ct
    )
    {
        // Resolve effective format
        var effectiveFormat = ResolveFormat(settings);

        // --json / --format json both suppress the spinner and table output
        var jsonMode = effectiveFormat is "json" or "json-nested";
        provider.ApplyOutputModeOverrides(settings.Json || jsonMode);

        BellaClient client;
        try
        {
            client = provider.CreateClient(settings.App);
        }
        catch (InvalidOperationException)
        {
            output.WriteError("Not logged in. Run 'bella login' first.");
            return 1;
        }

        try
        {
            var (projectSlug, _, _) = await context.ResolveProjectAsync(
                settings.Project,
                client,
                ct
            );
            var (envSlug, _, _) = await context.ResolveEnvironmentAsync(
                settings.Environment,
                projectSlug,
                client,
                ct
            );

            Dictionary<string, string> allSecrets = new();

            await AnsiConsole
                .Status()
                .StartAsync(
                    "Downloading secrets...",
                    async _ =>
                    {
                        if (!string.IsNullOrWhiteSpace(settings.Provider))
                        {
                            var payload = await client
                                .Api.V1.Projects[projectSlug]
                                .Environments[envSlug]
                                .Providers[settings.Provider]
                                .Secrets.GetAsync(cancellationToken: ct);
                            if (
                                payload?.AdditionalData.TryGetValue("secrets", out var rawSecrets)
                                == true
                            )
                                allSecrets = rawSecrets.ToStringDict();
                        }
                        else
                        {
                            var resp = await client
                                .Api.V1.Projects[projectSlug]
                                .Environments[envSlug]
                                .Secrets.GetAsync(cancellationToken: ct);
                            if (resp?.Secrets?.AdditionalData is not null)
                                allSecrets = resp.Secrets.AdditionalData.ToStringDict();
                        }
                    }
                );

            if (allSecrets.Count == 0)
            {
                output.WriteInfo("No secrets found.");
                return 0;
            }

            // ── Write to file ─────────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(settings.OutputFile))
            {
                var content = effectiveFormat switch
                {
                    "json"        => FormatJson(allSecrets, indented: true),
                    "json-nested" => FormatJsonNested(allSecrets),
                    _             => FormatEnv(allSecrets),
                };
                await File.WriteAllTextAsync(settings.OutputFile, content, ct);
                output.WriteSuccess(
                    $"Secrets written to '{settings.OutputFile}' ({allSecrets.Count} keys)"
                );
                return 0;
            }

            // ── stdout: JSON modes ─────────────────────────────────────────
            if (effectiveFormat == "json")
            {
                Console.WriteLine(FormatJson(allSecrets, indented: false));
                return 0;
            }

            if (effectiveFormat == "json-nested")
            {
                Console.WriteLine(FormatJsonNested(allSecrets));
                return 0;
            }

            // ── stdout: human-readable .env style ─────────────────────────
            var divider = new string('-', 60);
            AnsiConsole.WriteLine(divider);
            foreach (var kvp in allSecrets.OrderBy(k => k.Key))
                AnsiConsole.WriteLine($"{kvp.Key}={kvp.Value}");
            AnsiConsole.WriteLine(divider);
            output.WriteInfo($"{allSecrets.Count} secrets");

            return 0;
        }
        catch (InvalidOperationException ex)
        {
            output.WriteError(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to get secrets: {ex.Message}");
            return 1;
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Resolves the effective format string from settings.
    /// Priority: explicit --format > auto-detect from -o extension > --json alias > "env"
    /// </summary>
    private static string ResolveFormat(GetSecretsSettings settings)
    {
        // Explicit --format always wins
        if (!string.IsNullOrWhiteSpace(settings.Format))
            return settings.Format.ToLowerInvariant();

        // Auto-detect from output file extension
        if (!string.IsNullOrWhiteSpace(settings.OutputFile))
        {
            var ext = Path.GetExtension(settings.OutputFile).ToLowerInvariant();
            if (ext == ".json") return "json-nested";
        }

        // Deprecated --json flag
        if (settings.Json) return "json";

        return "env";
    }

    private static string FormatEnv(Dictionary<string, string> secrets)
    {
        var lines = secrets
            .OrderBy(kv => kv.Key)
            .Select(kv =>
            {
                var v = kv.Value;
                var safe = v.Contains(' ') || v.Contains('#') || v.Contains('"') || v.Contains('\n')
                    ? $"\"{v.Replace("\\", "\\\\").Replace("\"", "\\\"")}\""
                    : v;
                return $"{kv.Key}={safe}";
            });
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static string FormatJson(Dictionary<string, string> secrets, bool indented) =>
        JsonSerializer.Serialize(
            secrets.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value),
            new JsonSerializerOptions { WriteIndented = indented }
        );

    private static string FormatJsonNested(Dictionary<string, string> secrets)
    {
        var root = new Dictionary<string, object>(StringComparer.Ordinal);

        foreach (var (key, value) in secrets.OrderBy(kv => kv.Key))
        {
            var segments = HierarchySeparator.Split(key);
            if (segments.Length == 1)
            {
                root[key] = value;
                continue;
            }

            var current = root;
            for (var i = 0; i < segments.Length - 1; i++)
            {
                var seg = segments[i];
                if (!current.TryGetValue(seg, out var existing))
                {
                    var nested = new Dictionary<string, object>(StringComparer.Ordinal);
                    current[seg] = nested;
                    current = nested;
                }
                else if (existing is Dictionary<string, object> child)
                {
                    current = child;
                }
                else
                {
                    var promoted = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        [""] = existing,
                    };
                    current[seg] = promoted;
                    current = promoted;
                }
            }
            current[segments[^1]] = value;
        }

        return JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
    }
}
