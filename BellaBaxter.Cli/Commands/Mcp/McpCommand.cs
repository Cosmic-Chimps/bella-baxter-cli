using System.ComponentModel;
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
/// Bella Baxter /mcp endpoint (StreamableHttp, JWT-authenticated).
///
/// AI hosts (Claude Desktop, VS Code Copilot, Cursor…) launch this process
/// and communicate via stdin/stdout JSON-RPC.  All diagnostic output goes
/// to stderr so the stdio channel stays clean.
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

    public override async Task<int> ExecuteAsync(
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
        // Priority order:
        //   1. BELLA_BAXTER_API_KEY env var  (ideal for Claude Desktop / VS Code config)
        //   2. Stored API key from `bella login --api-key bax-...`
        //   3. Stored OAuth JWT from `bella login` (browser PKCE flow)
        //
        // API key auth → HMAC-signed HttpClient injected into HttpClientTransport.
        // OAuth JWT auth → static Bearer header (existing behaviour).

        var mcpUrl = $"{apiBase}/mcp";

        HttpClientTransport upstreamTransport;

        var envApiKey = Environment.GetEnvironmentVariable("BELLA_BAXTER_API_KEY");
        var storedApiKey = envApiKey is not null ? null : credentials.LoadApiKey();
        var rawApiKey = envApiKey ?? storedApiKey?.Raw;

        if (rawApiKey is not null)
        {
            // API key path — uses per-request HMAC signing via HmacSigningHandler.
            // No prior `bella login` needed.
            await Console.Error.WriteLineAsync($"[bella-mcp] Auth: API key (HMAC)");
            var hmacHandler = new HmacSigningHandler(rawApiKey, bellaClient: "bella-mcp");
            hmacHandler.InnerHandler = new HttpClientHandler();
            var hmacHttpClient = new HttpClient(hmacHandler);
            upstreamTransport = new HttpClientTransport(
                new HttpClientTransportOptions { Endpoint = new Uri(mcpUrl) },
                hmacHttpClient,
                loggerFactory: null,
                ownsHttpClient: true
            );
        }
        else
        {
            // OAuth JWT path — requires `bella login` first.
            var tokens = credentials.LoadTokens();
            var token = tokens?.AccessToken;

            if (string.IsNullOrEmpty(token))
            {
                await Console.Error.WriteLineAsync(
                    "[bella-mcp] ERROR: Not authenticated.\n"
                        + "Options:\n"
                        + "  1. Set BELLA_BAXTER_API_KEY=bax-... in the MCP server env config (recommended)\n"
                        + "  2. Run 'bella login' for interactive browser login"
                );
                return 1;
            }

            await Console.Error.WriteLineAsync($"[bella-mcp] Auth: OAuth JWT");
            upstreamTransport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(mcpUrl),
                    AdditionalHeaders = new Dictionary<string, string>
                    {
                        ["Authorization"] = $"Bearer {token}",
                    },
                }
            );
        }

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

                // Proxy tool calls → upstream with the stored JWT
                CallToolHandler = async (request, ct2) =>
                    await upstream.CallToolAsync(request.Params!, ct2),
            },
        };

        var stdioTransport = new StdioServerTransport(ServerName, loggerFactory: null);
        var server = McpServer.Create(stdioTransport, serverOptions, null, null);

        await Console.Error.WriteLineAsync("[bella-mcp] Ready — waiting for tool calls");
        await server.RunAsync(ct);

        await upstream.DisposeAsync();
        return 0;
    }

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

        var claudeLogin = isDefault
            ? new
            {
                command = "bella",
                args = new[] { "mcp" },
                env = (Dictionary<string, string>?)null,
            }
            : new
            {
                command = "bella",
                args = new[] { "mcp" },
                env = (Dictionary<string, string>?)
                    new Dictionary<string, string> { ["BELLA_API_URL"] = apiBase },
            };

        var claudeConfig = new
        {
            mcpServers = new Dictionary<string, object>
            {
                [$"{ServerName} (API key — recommended)"] = claudeApiKey,
                [$"{ServerName} (after bella login)"] = (object)claudeLogin,
            },
        };

        Console.WriteLine("\n── Claude Desktop ──────────────────────────────────────────");
        Console.WriteLine(
            "File: ~/Library/Application Support/Claude/claude_desktop_config.json\n"
        );
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(claudeConfig, PrettyJson));
        Console.WriteLine("  ⓘ  Get an API key from the Bella Baxter WebApp → Settings → API Keys");

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
