using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Text.Json;
using BellaBaxter.Client;
using BellaCli.Services;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Mcp;

public class McpSettings : CommandSettings
{
    [CommandOption("--api-url <url>")]
    [Description("Override Bella API base URL.")]
    public string? ApiUrl { get; init; }

    [CommandOption("--print-config")]
    [Description("Print Claude Desktop and VS Code MCP config snippets then exit.")]
    public bool PrintConfig { get; init; }
}

/// <summary>
/// Starts a local MCP server over stdio that proxies all tool calls to the
/// Bella Baxter /mcp endpoint (StreamableHttp, HMAC-signed API key).
///
/// MCP is a long-running M2M process — it requires an API key (bax-...) rather
/// than an OAuth JWT.  OAuth tokens expire after minutes; API keys do not.
///
/// Auth priority:
///   1. BELLA_BAXTER_API_KEY env var  (recommended — set in your MCP host config)
///   2. Stored API key from `bella login --api-key bax-...`
///
/// Run `bella mcp --print-config` to get the exact config snippet to paste.
/// </summary>
public class McpCommand(ConfigService config, CredentialStore credentials)
    : AsyncCommand<McpSettings>
{
    private const string ServerName = "bella-baxter";
    private const string CliVersion = "0.1.0";

    private static readonly System.Text.Json.JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
    };

    // ── In-memory secrets cache (lives for the duration of this bella mcp process) ──────────
    //
    // When an AI agent calls get_secret or list_secret_keys, we first send a conditional GET
    // to /secrets/hash with If-None-Match. If the server returns 304 Not Modified, we serve
    // the cached result without hitting /mcp at all (zero metered calls). Only when secrets
    // actually change (200 with new ETag) do we forward to upstream.
    //
    // /secrets/hash is excluded from metering (same category as getEnvironmentSecretsVersion)
    // so the conditional check is free regardless of outcome.
    //
    // Cache key:    "{projectSlug}/{envSlug}/{providerSlug}"
    // Result key:   "list_secret_keys"  OR  "get_secret:{keyName}"

    private sealed record EnvCacheEntry(
        string ETag,
        ConcurrentDictionary<string, CallToolResult> Results
    );

    private readonly ConcurrentDictionary<string, EnvCacheEntry> _secretsCache = new();

    private static readonly HashSet<string> _cachedTools = ["get_secret", "list_secret_keys"];

    protected override async Task<int> ExecuteAsync(
        CommandContext ctx,
        McpSettings settings,
        CancellationToken ct
    )
    {
        var apiBase = (settings.ApiUrl ?? config.ApiUrl).TrimEnd('/');

        if (settings.PrintConfig)
        {
            PrintConfigSnippets(apiBase);
            return 0;
        }

        // ── Auth ─────────────────────────────────────────────────────────────
        // MCP is a long-running M2M process — API key auth only.
        // OAuth tokens expire in minutes; API keys do not expire.
        //
        // Priority:
        //   1. BELLA_BAXTER_API_KEY env var  (recommended — set in MCP host config)
        //   2. Stored API key from `bella login --api-key bax-...`
        //
        // All requests are HMAC-signed via HmacSigningHandler (never a static Bearer header).

        var mcpUrl = $"{apiBase}/mcp";

        var envApiKey = Environment.GetEnvironmentVariable("BELLA_BAXTER_API_KEY");
        var storedApiKey = envApiKey is not null ? null : credentials.LoadApiKey();
        var rawApiKey = envApiKey ?? storedApiKey?.Raw;

        if (rawApiKey is null)
        {
            await Console.Error.WriteLineAsync(
                "[bella-mcp] ERROR: No API key found.\n"
                    + "\n"
                    + "bella mcp requires an API key — OAuth login is not supported for MCP\n"
                    + "because MCP is a long-running M2M process and OAuth tokens expire.\n"
                    + "\n"
                    + "To fix:\n"
                    + "  1. Create an API key in the Bella Baxter WebApp → Settings → API Keys\n"
                    + "  2. Set BELLA_BAXTER_API_KEY=bax-<your-key> in your MCP host config\n"
                    + "     OR run: bella login --api-key bax-<your-key>"
            );
            return 1;
        }

        await Console.Error.WriteLineAsync("[bella-mcp] Auth: API key (HMAC)");
        var hmacHandler = new HmacSigningHandler(rawApiKey, bellaClient: "bella-mcp");
        hmacHandler.InnerHandler = new HttpClientHandler();
        var hmacHttpClient = new HttpClient(hmacHandler);

        // Separate HttpClient for /secrets/hash conditional GETs (not owned by MCP transport).
        var hashHmacHandler = new HmacSigningHandler(rawApiKey, bellaClient: "bella-mcp");
        hashHmacHandler.InnerHandler = new HttpClientHandler();
        using var hashHttpClient = new HttpClient(hashHmacHandler);

        HttpClientTransport upstreamTransport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(mcpUrl) },
            hmacHttpClient,
            loggerFactory: null,
            ownsHttpClient: true
        );

        await Console.Error.WriteLineAsync($"[bella-mcp] Connecting to {mcpUrl}");

        McpClient upstream;
        try
        {
            upstream = await McpClient.CreateAsync(upstreamTransport, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(
                $"[bella-mcp] Failed to connect to {mcpUrl}: {ex.Message}"
            );
            return 1;
        }

        var toolList = await upstream.ListToolsAsync(cancellationToken: ct);
        await Console.Error.WriteLineAsync($"[bella-mcp] {toolList.Count} tools ready");

        // ── Local stdio server that proxies to upstream ───────────────────────
        var serverOptions = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = ServerName, Version = CliVersion },
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
            Handlers = new McpServerHandlers
            {
                // Proxy list_tools → upstream (live so tool-list changes propagate)
                ListToolsHandler = async (_, ct2) =>
                {
                    var fresh = await upstream.ListToolsAsync(cancellationToken: ct2);
                    return new ListToolsResult
                    {
                        Tools = fresh.Select(t => t.ProtocolTool).ToList(),
                    };
                },

                // Proxy tool calls → upstream.
                // For get_secret / list_secret_keys: serve from in-memory cache when possible.
                CallToolHandler = async (request, ct2) =>
                {
                    var toolName = request.Params!.Name;

                    if (_cachedTools.Contains(toolName))
                    {
                        var (envKey, resultKey) = ExtractCacheInfo(toolName, request.Params.Arguments);
                        if (envKey is not null && resultKey is not null)
                        {
                            return await CallWithCacheAsync(
                                envKey, resultKey, apiBase, request.Params,
                                hashHttpClient, upstream, ct2);
                        }
                    }

                    return await upstream.CallToolAsync(request.Params!, ct2);
                },
            },
        };

        var stdioTransport = new StdioServerTransport(ServerName, loggerFactory: null);
        var server = McpServer.Create(stdioTransport, serverOptions, null, null);

        await Console.Error.WriteLineAsync("[bella-mcp] Ready — waiting for tool calls");
        await server.RunAsync(ct);

        await upstream.DisposeAsync();
        return 0;
    }

    // ── Cache helpers ─────────────────────────────────────────────────────────

    private async Task<CallToolResult> CallWithCacheAsync(
        string envKey,
        string resultKey,
        string apiBase,
        CallToolRequestParams toolParams,
        HttpClient hashHttpClient,
        McpClient upstream,
        CancellationToken ct)
    {
        if (_secretsCache.TryGetValue(envKey, out var entry))
        {
            var (etagChanged, newETag) = await CheckETagAsync(apiBase, envKey, entry.ETag, hashHttpClient, ct);

            if (!etagChanged)
            {
                // 304 Not Modified — secrets unchanged
                if (entry.Results.TryGetValue(resultKey, out var cached))
                    return cached;  // cache hit — zero metered calls

                // ETag still valid but this specific result not yet cached — fall through to fetch
            }
            else if (newETag is not null)
            {
                // Secrets changed — evict stale results, update ETag
                _secretsCache[envKey] = new EnvCacheEntry(newETag, new ConcurrentDictionary<string, CallToolResult>());
            }
            // If ETag check failed (network error etc.) we fall through and forward without caching
        }

        // Forward to upstream
        var result = await upstream.CallToolAsync(toolParams, ct);

        // Cache the result
        if (_secretsCache.TryGetValue(envKey, out var existingEntry))
        {
            existingEntry.Results[resultKey] = result;
        }
        else
        {
            // First call for this env — fetch ETag to seed the cache
            var etag = await FetchInitialETagAsync(apiBase, envKey, hashHttpClient, ct);
            var newEntry = new EnvCacheEntry(etag, new ConcurrentDictionary<string, CallToolResult>());
            newEntry.Results[resultKey] = result;
            _secretsCache.TryAdd(envKey, newEntry);
        }

        return result;
    }

    /// <summary>
    /// Sends a conditional GET to /secrets/hash with If-None-Match.
    /// Returns (changed: false, newETag: null) on 304, (changed: true, newETag) on 200,
    /// (changed: false, null) on error (fail open — keep serving from cache).
    /// </summary>
    private static async Task<(bool Changed, string? NewETag)> CheckETagAsync(
        string apiBase, string envKey, string etag, HttpClient client, CancellationToken ct)
    {
        try
        {
            var url = BuildHashUrl(apiBase, envKey);
            if (url is null) return (false, null);

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("If-None-Match", etag);
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

            if (resp.StatusCode == HttpStatusCode.NotModified)
                return (false, null);

            if (resp.IsSuccessStatusCode)
            {
                var newETag = resp.Headers.ETag?.ToString() ?? "";
                return (true, newETag);
            }

            return (false, null); // unexpected status — fail open
        }
        catch
        {
            return (false, null); // network error — fail open, serve from cache
        }
    }

    /// <summary>Fetches /secrets/hash to get the initial ETag for a newly-cached env.</summary>
    private static async Task<string> FetchInitialETagAsync(
        string apiBase, string envKey, HttpClient client, CancellationToken ct)
    {
        try
        {
            var url = BuildHashUrl(apiBase, envKey);
            if (url is null) return "";

            using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            return resp.IsSuccessStatusCode ? (resp.Headers.ETag?.ToString() ?? "") : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Extracts (envKey, resultKey) from tool call arguments.
    /// envKey:    "projectSlug/environmentSlug/providerSlug"
    /// resultKey: "list_secret_keys"  OR  "get_secret:{keyName}"
    /// Returns (null, null) if required params are missing.
    /// </summary>
    private static (string? EnvKey, string? ResultKey) ExtractCacheInfo(
        string toolName,
        IDictionary<string, JsonElement>? args)
    {
        if (args is null) return (null, null);

        var project  = GetStringArg(args, "projectSlug");
        var env      = GetStringArg(args, "environmentSlug");
        var provider = GetStringArg(args, "providerSlug");

        if (project is null || env is null || provider is null) return (null, null);

        var envKey = $"{project}/{env}/{provider}";
        var resultKey = toolName switch
        {
            "list_secret_keys" => "list_secret_keys",
            "get_secret"       => $"get_secret:{GetStringArg(args, "key") ?? ""}",
            _                  => null,
        };

        return (envKey, resultKey);
    }

    /// <summary>Builds the /secrets/hash URL from an envKey "project/env/provider".</summary>
    private static string? BuildHashUrl(string apiBase, string envKey)
    {
        var parts = envKey.Split('/', 3);
        return parts.Length == 3
            ? $"{apiBase}/api/v1/projects/{parts[0]}/environments/{parts[1]}/providers/{parts[2]}/secrets/hash"
            : null;
    }

    private static string? GetStringArg(IDictionary<string, JsonElement> args, string key)
        => args.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    // ── Config snippets ───────────────────────────────────────────────────────

    private static void PrintConfigSnippets(string apiBase)
    {
        bool isDefault = apiBase == "https://api.bella-baxter.io";

        // ── Claude Desktop ────────────────────────────────────────────────────
        // Show both: API key (recommended) and bare (for `bella login` users)
        var claudeApiKey = new
        {
            command = "bella",
            args = new[] { "mcp" },
            env = new Dictionary<string, string> { ["BELLA_BAXTER_API_KEY"] = "bax-<your-api-key>" }
                .Concat(
                    isDefault ? [] : [new KeyValuePair<string, string>("BELLA_API_URL", apiBase)]
                )
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
        };

        var claudeConfig = new
        {
            mcpServers = new Dictionary<string, object>
            {
                [ServerName] = claudeApiKey,
            },
        };

        Console.WriteLine("\n── Claude Desktop ──────────────────────────────────────────");
        Console.WriteLine(
            "File: ~/Library/Application Support/Claude/claude_desktop_config.json\n"
        );
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(claudeConfig, PrettyJson));
        Console.WriteLine("  ⓘ  Get an API key from the Bella Baxter WebApp → Settings → API Keys");
        Console.WriteLine("  ⓘ  bella mcp requires an API key — OAuth login is not supported (tokens expire)");

        // ── VS Code / GitHub Copilot ──────────────────────────────────────────
        object vscodeEntry = new
        {
            type = "stdio",
            command = "bella",
            args = new[] { "mcp" },
            env = new Dictionary<string, string> { ["BELLA_BAXTER_API_KEY"] = "bax-<your-api-key>" }
                .Concat(
                    isDefault ? [] : [new KeyValuePair<string, string>("BELLA_API_URL", apiBase)]
                )
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
        };

        var vscodeConfig = new
        {
            servers = new Dictionary<string, object> { [ServerName] = vscodeEntry },
        };

        Console.WriteLine("\n── VS Code / GitHub Copilot ────────────────────────────────");
        Console.WriteLine("File: .vscode/mcp.json  (workspace)  or  User settings.json\n");
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(vscodeConfig, PrettyJson));

        // ── Available tools ───────────────────────────────────────────────────
        Console.WriteLine("\n── Available MCP tools ─────────────────────────────────────");
        Console.WriteLine(
            string.Join(
                "\n",
                [
                    "  list_projects      — list projects you have access to",
                    "  list_environments  — list environments for a project",
                    "  list_providers     — list secret providers for an environment",
                    "  list_secret_keys   — list secret key names (values never exposed)",
                    "  get_secret         — retrieve a specific secret value",
                    "  set_secret         — create or update a secret",
                    "  delete_secret      — permanently delete a secret",
                    "  get_totp_code      — generate a current TOTP/2FA code",
                    "  list_totp_keys     — list TOTP key names",
                    "  sign_ssh_key       — sign an SSH public key via Vault CA",
                    "  list_ssh_roles     — list available SSH CA roles",
                    "  bella_issue_token  — issue a short-lived, scope-limited token for the current task",
                ]
            )
        );
        Console.WriteLine();
    }
}
