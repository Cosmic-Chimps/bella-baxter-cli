using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Auth;

public class AuthStatusSettings : CommandSettings { }

public class AuthStatusCommand(CredentialStore credentials, IOutputWriter output)
    : AsyncCommand<AuthStatusSettings>
{
    public override Task<int> ExecuteAsync(
        CommandContext context,
        AuthStatusSettings settings,
        CancellationToken ct
    )
    {
        if (!credentials.IsAuthenticated())
        {
            output.WriteError(
                "Not logged in. Run 'bella login' to authenticate.",
                "unauthenticated"
            );
            return Task.FromResult(1);
        }

        if (credentials.IsApiKeyMode())
        {
            var key = credentials.LoadApiKey()!;
            output.WriteObject(
                new
                {
                    authType = "api_key",
                    keyId = key.KeyId,
                    status = "active",
                }
            );
            return Task.FromResult(0);
        }

        var tokens = credentials.LoadTokens()!;
        var expired = tokens.ExpiresAt <= DateTimeOffset.UtcNow;
        output.WriteObject(
            new
            {
                authType = "oauth2",
                expiresAt = tokens.ExpiresAt,
                expired,
                status = expired ? "expired" : "active",
            }
        );

        return Task.FromResult(0);
    }
}

public class AuthRefreshSettings : CommandSettings { }

public class AuthRefreshCommand(AuthService auth, IOutputWriter output)
    : AsyncCommand<AuthRefreshSettings>
{
    public override async Task<int> ExecuteAsync(
        CommandContext context,
        AuthRefreshSettings settings,
        CancellationToken ct
    )
    {
        try
        {
            StoredTokens tokens = null!;
            await AnsiConsole
                .Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync(
                    "Refreshing token...",
                    async _ =>
                    {
                        tokens = await auth.RefreshAsync(ct);
                    }
                );

            output.WriteSuccess($"Token refreshed. New expiry: {tokens.ExpiresAt:u}");
            return 0;
        }
        catch (Exception ex)
        {
            output.WriteError($"Token refresh failed: {ex.Message}", "refresh_failed");
            return 1;
        }
    }
}
