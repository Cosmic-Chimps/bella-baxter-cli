using System.ComponentModel;
using BellaBaxter.Client;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Microsoft.Kiota.Abstractions;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Me;

public class WhoAmISettings : CommandSettings
{
    [CommandOption("--offline")]
    [Description(
        "Report the identity from the local credential cache without contacting the server. "
            + "Does not prove the credential still works."
    )]
    public bool Offline { get; init; }
}

/// <summary>
/// Pilot F9: <c>whoami</c> decoded the stored JWT locally — no signature check, no <c>exp</c>
/// check, errors swallowed — and printed the org snapshotted at login. It never called the server
/// and always exited 0, so a revoked session, a revoked API key and an unreachable server were
/// indistinguishable from a working one. An identity command that cannot fail is not an identity
/// command; scripts that gate on it were gating on the presence of a file.
///
/// It now asks the server by default. <c>--offline</c> keeps the old behaviour, named for what it
/// actually is.
/// </summary>
public class WhoAmICommand(
    CredentialStore credentials,
    KeyContextService keyContext,
    BellaClientProvider clientProvider,
    IOutputWriter output
) : AsyncCommand<WhoAmISettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        WhoAmISettings settings,
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

        return credentials.IsApiKeyMode()
            ? await ReportApiKeyAsync(settings, ct)
            : await ReportOAuthAsync(settings, ct);
    }

    // ── API key ─────────────────────────────────────────────────────────────
    private async Task<int> ReportApiKeyAsync(WhoAmISettings settings, CancellationToken ct)
    {
        var key = credentials.LoadApiKey()!;

        // keyPrefix is what the console's API Keys listing shows (bax-XXXXXXXX); keyId is the full id.
        if (settings.Offline)
        {
            output.WriteObject(
                new
                {
                    authType = "api_key",
                    keyPrefix = key.KeyPrefix,
                    keyId = key.KeyId,
                    source = "local",
                }
            );
            return 0;
        }

        var (ctx, failure) = await keyContext.TryDiscoverAsync(ct);

        if (failure == ServerProbeFailure.Rejected)
        {
            output.WriteError(
                "API key revoked or invalid. Create a new key and run 'bella login --api-key'.",
                "key_rejected"
            );
            return 1;
        }

        if (ctx is null)
        {
            output.WriteError(
                "Could not reach the server to verify the API key. Use 'bella whoami --offline' to "
                    + "print the cached identity.",
                "unreachable"
            );
            return 1;
        }

        output.WriteObject(
            new
            {
                authType = "api_key",
                keyPrefix = key.KeyPrefix,
                keyId = key.KeyId,
                role = ctx.Role,
                org = ctx.OrgSlug,
                project = ctx.ProjectSlug,
                environment = ctx.EnvironmentSlug,
                source = "server",
            }
        );
        return 0;
    }

    // ── OAuth session ───────────────────────────────────────────────────────
    private async Task<int> ReportOAuthAsync(WhoAmISettings settings, CancellationToken ct)
    {
        var tokens = credentials.LoadTokens()!;

        // Org from stored tokens (set at login/refresh from JWT claims).
        var orgDisplay = (tokens.OrgName, tokens.OrgSlug) switch
        {
            ({ } n, { } s) => $"{n} ({s})",
            ({ } n, null) => n,
            (null, { } s) => s,
            _ => null,
        };

        if (settings.Offline)
        {
            var claims = AuthService.DecodeJwtPayload(tokens.AccessToken);
            output.WriteObject(
                new
                {
                    authType = "oauth2",
                    sub = AuthService.GetClaim(claims, "sub"),
                    username = AuthService.GetClaim(claims, "preferred_username"),
                    name = AuthService.GetClaim(claims, "name"),
                    email = AuthService.GetClaim(claims, "email"),
                    org = orgDisplay,
                    source = "local",
                }
            );
            return 0;
        }

        BellaClient client;
        try
        {
            client = clientProvider.CreateClient();
        }
        catch (Exception ex)
        {
            output.WriteError(ex.Message, "unauthenticated");
            return 1;
        }

        try
        {
            // The client's TokenRefreshHandler renews a nearly-expired session on the way out and
            // raises SessionExpiredException when it cannot.
            var me = await client.Api.V1.Users.Me.GetAsync(cancellationToken: ct);

            if (me is null)
            {
                output.WriteError(
                    "The server did not recognise this session. Run 'bella login'.",
                    "session_expired"
                );
                return 1;
            }

            output.WriteObject(
                new
                {
                    authType = "oauth2",
                    // Kept from the token so the output stays a SUPERSET of what --offline and
                    // every previous version printed — a script parsing `sub` must not break.
                    sub = AuthService.GetClaim(
                        AuthService.DecodeJwtPayload(tokens.AccessToken),
                        "sub"
                    ),
                    id = me.Id,
                    username = me.Username,
                    name = me.Nombre,
                    email = me.Email,
                    isAdmin = me.IsAdmin,
                    org = orgDisplay,
                    source = "server",
                }
            );
            return 0;
        }
        catch (OperationCanceledException)
        {
            output.WriteError("Cancelled.", "cancelled");
            return 1;
        }
        catch (Exception ex)
        {
            if (ServerProbe.Classify(ex) == ServerProbeFailure.Rejected)
            {
                output.WriteError("Session expired. Run 'bella login'.", "session_expired");
                return 1;
            }

            var detail = ex is ApiException api ? $" (HTTP {api.ResponseStatusCode})" : "";
            output.WriteError(
                $"Could not reach the server to verify this session{detail}. Use "
                    + "'bella whoami --offline' to print the cached identity.",
                "unreachable"
            );
            return 1;
        }
    }
}
