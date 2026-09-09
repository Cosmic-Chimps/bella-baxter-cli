using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Auth;

public class AuthStatusSettings : CommandSettings { }

public class AuthStatusCommand(
    CredentialStore credentials,
    ZkeService zke,
    BellaClientProvider provider,
    IOutputWriter output
) : AsyncCommand<AuthStatusSettings>
{
    protected override async Task<int> ExecuteAsync(
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
            return 1;
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
            return 0;
        }

        var tokens = credentials.LoadTokens()!;
        var expired = tokens.ExpiresAt <= DateTimeOffset.UtcNow;

        // spec 037 — the two things a person needs before they hit a 403 they cannot interpret: does
        // this tenant require a registered device, and is THIS machine's key one? Both come from
        // getZkeStatus, which every member can read (before spec 037 the flag was Owner-only, so the
        // CLI could not answer either question — research R7).
        var (deviceLine, enforcement) = await DescribeZkeAsync(expired, ct);

        output.WriteObject(
            new
            {
                authType = "oauth2",
                expiresAt = tokens.ExpiresAt,
                expired,
                status = expired ? "expired" : "active",
                tenant = tokens.OrgSlug,
                device = deviceLine,
                zkeEnforcement = enforcement,
            }
        );

        // Pilot F7: an expired session is not a healthy status. Exiting 0 here made
        // `bella auth status` useless as a script probe — the caller had to parse the payload
        // to learn what the exit code should already have told it.
        return expired ? 1 : 0;
    }

    private async Task<(string Device, string Enforcement)> DescribeZkeAsync(bool expired, CancellationToken ct)
    {
        var fingerprint = BellaBaxter.Crypto.DeviceFingerprint.ComputeFromBase64(zke.GetPublicKey());

        if (fingerprint is null)
            return ("none", expired ? "unknown" : await EnforcementOnlyAsync(ct));

        // An expired session cannot ask, and reporting "not registered" on the strength of a call we
        // could not make would send someone to re-register a device that is registered.
        if (expired)
            return ($"{fingerprint} (registration unknown — session expired)", "unknown");

        try
        {
            var status = await provider.CreateClient().Api.V1.Tenants.Me.Zke.GetAsync(cancellationToken: ct);
            var registered = status?.PresentedKeyRegistered ?? false;
            var label = status?.PresentedKeyLabel;

            return (
                registered
                    ? $"{fingerprint}{(label is null ? "" : $" ({label})")} — registered in this tenant"
                    : $"{fingerprint} — NOT registered in this tenant; run 'bella auth setup'",
                (status?.Enforced ?? false) ? "on" : "off");
        }
        catch
        {
            return ($"{fingerprint} (registration unknown — could not reach the server)", "unknown");
        }
    }

    private async Task<string> EnforcementOnlyAsync(CancellationToken ct)
    {
        try
        {
            var status = await provider.CreateClient().Api.V1.Tenants.Me.Zke.GetAsync(cancellationToken: ct);
            return (status?.Enforced ?? false) ? "on" : "off";
        }
        catch
        {
            return "unknown";
        }
    }
}

public class AuthRefreshSettings : CommandSettings { }

public class AuthRefreshCommand(AuthService auth, IOutputWriter output)
    : AsyncCommand<AuthRefreshSettings>
{
    protected override async Task<int> ExecuteAsync(
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
