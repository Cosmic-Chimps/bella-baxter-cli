using System.ComponentModel;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands;

public class LoginSettings : CommandSettings
{
    /// <summary>
    /// The API key, inline. Issue #833: a key passed as an argument is visible in <c>ps</c> for the
    /// life of the process and lands in shell history — and this key does not expire, so the
    /// exposure does not either. Kept because removing it would break existing scripts; no longer
    /// recommended anywhere, and <c>bella login</c> with no flag asks for it on a hidden prompt.
    /// </summary>
    [CommandOption("--api-key <KEY>")]
    [Description("The API key. Visible in `ps` and shell history — prefer plain `bella login`")]
    public string? ApiKey { get; init; }

    [CommandOption("--force")]
    public bool Force { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class LoginCommand(AuthService auth, CredentialStore credentials, KeyContextService keyContext, ZkeService zke, ConfigService config, IOutputWriter output)
    : AsyncCommand<LoginSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        LoginSettings settings,
        CancellationToken ct
    )
    {
        switch (
            LoginGate.Decide(
                isAuthenticated: credentials.IsAuthenticated(),
                isApiKeyMode: credentials.IsApiKeyMode(),
                isOAuthTokenExpired: auth.IsTokenExpired(),
                force: settings.Force
            )
        )
        {
            case LoginAction.AlreadyLoggedIn:
                output.WriteWarning(
                    credentials.IsApiKeyMode()
                        ? "Already logged in with an API key. Use --force to re-authenticate."
                        : "Already logged in. Use --force to re-authenticate."
                );
                return 0;

            case LoginAction.TryRefresh:
                try
                {
                    var refreshed = await auth.RefreshAsync(ct);
                    output.WriteSuccess($"Session refreshed. Token expires at {refreshed.ExpiresAt:u}.");
                    return 0;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    output.WriteWarning(
                        "Stored session has expired and could not be refreshed — starting a new login."
                    );
                    // fall through to the login flow below
                }
                break;

            case LoginAction.StartLogin:
            default:
                break;
        }

        // ── API key mode ─────────────────────────────────────────────────────
        if (settings.ApiKey is not null)
        {
            try
            {
                auth.LoginWithApiKey(settings.ApiKey);
                output.WriteSuccess("API key stored successfully.");
                // Issue #833, and the reason it is said AFTER the success: the key is already
                // stored and already in the history, so this is advice for next time rather than a
                // refusal. Terminal only — in a pipeline the advice is unreadable and the shell
                // history it warns about does not exist. Same placement as #743's.
                if (Interactivity.IsInteractive(output))
                {
                    output.WriteWarning(
                        "That key is now in your shell history and was visible in `ps` while the "
                            + "command ran, and an API key does not expire. Next time run "
                            + "`bella login` with no flag — it prompts without echoing — or set "
                            + "BELLA_BAXTER_API_KEY for automation."
                    );
                }
                await TryWriteBellaContextAsync(ct);
                return 0;
            }
            catch (ArgumentException ex)
            {
                output.WriteError(ex.Message, "invalid_api_key");
                return 1;
            }
        }

        // ── Interactive: prompt for mode if human ────────────────────────────
        string mode;
        if (!Console.IsInputRedirected)
        {
            mode = await AnsiConsole.PromptAsync(
                new SelectionPrompt<string>()
                    .Title("How would you like to log in?")
                    .AddChoices("Browser (OAuth2)", "API Key (bax- token)"),
                ct
            );
        }
        else
        {
            mode = "Browser (OAuth2)";
        }

        if (mode.StartsWith("API"))
        {
            var key = await AnsiConsole.PromptAsync(
                new TextPrompt<string>("Enter your API key:").Secret().ClearOnFinish(),
                ct
            );
            try
            {
                auth.LoginWithApiKey(key);
                output.WriteSuccess("API key stored successfully.");
                await TryWriteBellaContextAsync(ct);
                return 0;
            }
            catch (ArgumentException ex)
            {
                output.WriteError(ex.Message, "invalid_api_key");
                return 1;
            }
        }

        // ── Browser OAuth2 PKCE flow ─────────────────────────────────────────
        try
        {
            StoredTokens tokens = null!;
            await output.StatusAsync(
                    "Opening browser for login...",
                    async setStatus =>
                    {
                        setStatus("Waiting for browser login...");
                        tokens = await auth.LoginWithBrowserAsync(ct);
                    }
                );

            output.WriteSuccess($"Logged in successfully. Token expires at {tokens.ExpiresAt:u}.");
            TryUpdateBellaOrg(tokens.OrgSlug);

            // ZKE hint — show once if device key not yet set up
            if (!zke.HasKeypair())
                AnsiConsole.MarkupLine(
                    "[dim]💡 Tip: run [cyan]bella auth setup[/] to enable zero-knowledge secret " +
                    "decryption so your secrets are decrypted locally by the CLI.[/]");

            return 0;
        }
        catch (OperationCanceledException)
        {
            output.WriteError("Login cancelled.", "cancelled");
            return 1;
        }
        catch (Exception ex)
        {
            output.WriteError($"Login failed: {ex.Message}", "login_failed");
            return 1;
        }
    }

    /// <summary>
    /// After OAuth login, if <paramref name="orgSlug"/> is known and a <c>.bella</c> file exists
    /// in the current directory tree, updates the <c>org</c> field in it. Silently skips if
    /// no <c>.bella</c> file is found or the slug is null.
    /// </summary>
    private static void TryUpdateBellaOrg(string? orgSlug)
    {
        if (orgSlug is null) return;
        var bellaFile = KeyContextService.FindBellaFile(Directory.GetCurrentDirectory());
        if (bellaFile is null) return;
        KeyContextService.UpdateBellaOrg(bellaFile, orgSlug);
        AnsiConsole.MarkupLine($"[dim]↺ Updated [cyan].bella[/] org → [cyan]{Markup.Escape(orgSlug)}[/][/]");
    }

    /// <summary>
    /// After API key login, try to call GET /api/v1/keys/me and write the key's context into the
    /// .bella file in the current directory (merging into an existing one — its url survives).
    /// Silently skips if the server is unreachable.
    /// </summary>
    private async Task TryWriteBellaContextAsync(CancellationToken ct)
    {
        var ctx = await keyContext.DiscoverAsync(ct);
        if (ctx is null) return;

        var dir = Directory.GetCurrentDirectory();
        var existing = Path.Combine(dir, ".bella");
        bool alreadyExists = File.Exists(existing);

        KeyContextService.WriteBellaFile(dir, ctx, config.ApiUrl);

        var scope = ctx.EnvironmentSlug is not null
            ? $"{Markup.Escape(ctx.ProjectSlug)}/{Markup.Escape(ctx.EnvironmentSlug)}"
            : Markup.Escape(ctx.ProjectSlug);

        if (alreadyExists)
            AnsiConsole.MarkupLine($"[dim]↺ Updated [cyan].bella[/] → [cyan]{scope}[/] (from API key)[/]");
        else
            AnsiConsole.MarkupLine($"[dim]✓ Created [cyan].bella[/] → [cyan]{scope}[/] (from API key)[/]");
    }
}
