using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands;

public class LoginSettings : CommandSettings
{
    [CommandOption("--api-key <KEY>")]
    public string? ApiKey { get; init; }

    [CommandOption("--force")]
    public bool Force { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class LoginCommand(AuthService auth, CredentialStore credentials, KeyContextService keyContext, IOutputWriter output)
    : AsyncCommand<LoginSettings>
{
    public override async Task<int> ExecuteAsync(
        CommandContext context,
        LoginSettings settings,
        CancellationToken ct
    )
    {
        if (credentials.IsAuthenticated() && !settings.Force)
        {
            output.WriteWarning("Already logged in. Use --force to re-authenticate.");
            return 0;
        }

        // ── API key mode ─────────────────────────────────────────────────────
        if (settings.ApiKey is not null)
        {
            try
            {
                auth.LoginWithApiKey(settings.ApiKey);
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

        // ── Interactive: prompt for mode if human ────────────────────────────
        string mode;
        if (!Console.IsInputRedirected)
        {
            mode = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("How would you like to log in?")
                    .AddChoices("Browser (OAuth2)", "API Key (bax- token)")
            );
        }
        else
        {
            mode = "Browser (OAuth2)";
        }

        if (mode.StartsWith("API"))
        {
            var key = AnsiConsole.Prompt(new TextPrompt<string>("Enter your API key:").Secret());
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
            await AnsiConsole
                .Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync(
                    "Opening browser for login...",
                    async ctx =>
                    {
                        ctx.Status("Waiting for browser login...");
                        tokens = await auth.LoginWithBrowserAsync(ct);
                    }
                );

            output.WriteSuccess($"Logged in successfully. Token expires at {tokens.ExpiresAt:u}.");
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
    /// After API key login, try to call GET /api/v1/keys/me and auto-write a .bella file
    /// in the current directory. Silently skips if the server is unreachable.
    /// </summary>
    private async Task TryWriteBellaContextAsync(CancellationToken ct)
    {
        var ctx = await keyContext.DiscoverAsync(ct);
        if (ctx is null) return;

        var dir = Directory.GetCurrentDirectory();
        var existing = Path.Combine(dir, ".bella");
        bool alreadyExists = File.Exists(existing);

        KeyContextService.WriteBellaFile(dir, ctx);

        var scope = ctx.EnvironmentSlug is not null
            ? $"{Markup.Escape(ctx.ProjectSlug)}/{Markup.Escape(ctx.EnvironmentSlug)}"
            : Markup.Escape(ctx.ProjectSlug);

        if (alreadyExists)
            AnsiConsole.MarkupLine($"[dim]↺ Updated [cyan].bella[/] → [cyan]{scope}[/] (from API key)[/]");
        else
            AnsiConsole.MarkupLine($"[dim]✓ Created [cyan].bella[/] → [cyan]{scope}[/] (from API key)[/]");
    }
}
