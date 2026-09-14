using BellaBaxter.Client;
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
    AuthService auth,
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

        // #741 — do what EVERY other command does before reporting: refresh.
        //
        // This used to compare `ExpiresAt` locally and stop there, so an access token that lapsed an
        // hour ago was reported `expired`/exit 1 while the very next `bella secrets list` refreshed
        // and succeeded. A CI pre-flight built on this therefore failed falsely once an hour, on a
        // session that was perfectly healthy.
        //
        // Note this does NOT revert "Pilot F7: an expired session is not a healthy status" below.
        // That rule was right and is kept — the exit code should tell a script the truth without it
        // having to parse the payload. What F7 got wrong was the PREMISE: it treated a lapsed access
        // token as an unusable session, when a session is unusable only once the REFRESH token is
        // gone. So the check moves from "is the access token stale" to "can this session still be
        // renewed", which is the question a pre-flight is actually asking.
        var tokens = credentials.LoadTokens()!;
        var refreshFailure = (string?)null;
        try
        {
            tokens = await auth.EnsureValidTokenAsync(ct);
        }
        catch (Exception ex)
        {
            // A refresh token that is expired, revoked or rejected. THIS is an unhealthy session:
            // no command will work until the operator logs in again.
            refreshFailure = ex.Message;
        }

        var expired = refreshFailure is not null || tokens.ExpiresAt <= DateTimeOffset.UtcNow;

        // spec 037 — the two things a person needs before they hit a 403 they cannot interpret: does
        // this tenant require a registered device, and is THIS machine's key one? Both come from
        // getZkeStatus, which every member can read (before spec 037 the flag was Owner-only, so the
        // CLI could not answer either question — research R7).
        var (deviceLine, enforcement, deviceRegistered, enforced) = await DescribeZkeAsync(expired, ct);

        output.WriteObject(
            new
            {
                authType = "oauth2",
                expiresAt = tokens.ExpiresAt,
                expired,
                status = expired ? "expired" : "active",
                tenant = tokens.OrgSlug,
                // #741 — BOOLEANS for the two facts a script branches on. `device` and
                // `zkeEnforcement` stay exactly as they were, because they are what a person reads
                // and removing them would break anyone already printing them; but grepping a
                // sentence for the word "registered" is not an interface, and the server has
                // answered `presentedKeyRegistered` as a boolean all along.
                deviceRegistered = deviceRegistered,
                enforced = enforced,
                device = deviceLine,
                zkeEnforcement = enforcement,
            }
        );

        // Pilot F7: an expired session is not a healthy status. Exiting 0 here made
        // `bella auth status` useless as a script probe — the caller had to parse the payload
        // to learn what the exit code should already have told it.
        return expired ? 1 : 0;
    }

    /// <summary>
    /// The device/enforcement facts, as BOTH a sentence for a person and booleans for a script (#741).
    /// </summary>
    /// <remarks>
    /// <see cref="bool"/>? rather than <see cref="bool"/> for <c>registered</c>: "we asked and the
    /// answer is no" and "we could not ask" are different facts, and collapsing them to <c>false</c>
    /// would tell an operator with a perfectly good device to re-register it. That is the same
    /// distinction the prose already draws with "registration unknown".
    /// </remarks>
    private async Task<(string Device, string Enforcement, bool? Registered, bool? Enforced)>
        DescribeZkeAsync(bool expired, CancellationToken ct)
    {
        var fingerprint = BellaBaxter.Crypto.DeviceFingerprint.ComputeFromBase64(zke.GetPublicKey());

        if (fingerprint is null)
        {
            // No key on this machine at all — a definite "not registered", not an unknown.
            var enforcementOnly = expired ? null : await EnforcementOnlyAsync(ct);
            return ("none", enforcementOnly ?? "unknown", false, ParseEnforced(enforcementOnly));
        }

        // An expired session cannot ask, and reporting "not registered" on the strength of a call we
        // could not make would send someone to re-register a device that is registered.
        if (expired)
            return ($"{fingerprint} (registration unknown — session expired)", "unknown", null, null);

        try
        {
            // #635 — the question here is "is MY key registered?", so the key has to be presented.
            // This used to call `provider.CreateClient()`, which carries no device key, so the server
            // saw no key, correctly answered `presentedKeyRegistered: false`, and `auth status` told a
            // correctly registered operator they were NOT registered. Note `EnforcementOnlyAsync`
            // below is right to use the plain client: it runs only when no device key exists and reads
            // just the tenant flag, which does not depend on what was presented.
            using var ecdh = zke.LoadEcdhKey();
            using var handler = ecdh is null ? null : new ZkeDekHandler(ecdh);

            var client = handler is null
                ? provider.CreateClient()
                : provider.CreateClientWithZke(handler);

            var status = await client.Api.V1.Tenants.Me.Zke.GetAsync(cancellationToken: ct);
            var registered = status?.PresentedKeyRegistered ?? false;
            var label = status?.PresentedKeyLabel;

            return (
                registered
                    ? $"{fingerprint}{(label is null ? "" : $" ({label})")} — registered in this tenant"
                    : $"{fingerprint} — NOT registered in this tenant; run 'bella auth setup'",
                (status?.Enforced ?? false) ? "on" : "off",
                registered,
                status?.Enforced ?? false);
        }
        catch
        {
            return ($"{fingerprint} (registration unknown — could not reach the server)", "unknown", null, null);
        }
    }

    /// <summary>The tenant's enforcement flag, or null when it could not be read.</summary>
    private async Task<string?> EnforcementOnlyAsync(CancellationToken ct)
    {
        try
        {
            var status = await provider.CreateClient().Api.V1.Tenants.Me.Zke.GetAsync(cancellationToken: ct);
            return (status?.Enforced ?? false) ? "on" : "off";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>"on"/"off" back to a boolean; null stays null rather than becoming false.</summary>
    private static bool? ParseEnforced(string? enforcement) =>
        enforcement switch
        {
            "on" => true,
            "off" => false,
            _ => null,
        };
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
            await output.StatusAsync("Refreshing token...", async () =>
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
