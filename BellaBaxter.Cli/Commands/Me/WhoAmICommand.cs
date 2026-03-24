using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Me;

public class WhoAmISettings : CommandSettings { }

public class WhoAmICommand(CredentialStore credentials, IOutputWriter output)
    : AsyncCommand<WhoAmISettings>
{
    public override Task<int> ExecuteAsync(
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
            return Task.FromResult(1);
        }

        if (credentials.IsApiKeyMode())
        {
            var key = credentials.LoadApiKey()!;
            output.WriteObject(new { authType = "api_key", keyId = key.KeyId });
            return Task.FromResult(0);
        }

        var tokens = credentials.LoadTokens()!;
        var claims = AuthService.DecodeJwtPayload(tokens.AccessToken);

        var sub = AuthService.GetClaim(claims, "sub");
        var name = AuthService.GetClaim(claims, "name");
        var email = AuthService.GetClaim(claims, "email");
        var username = AuthService.GetClaim(claims, "preferred_username");

        // Org from stored tokens (set at login/refresh from JWT claims)
        var orgName = tokens.OrgName;
        var orgSlug = tokens.OrgSlug;
        var orgDisplay = (orgName, orgSlug) switch
        {
            ({ } n, { } s) => $"{n} ({s})",
            ({ } n, null) => n,
            (null, { } s) => s,
            _ => null
        };

        output.WriteObject(
            new
            {
                authType = "oauth2",
                sub,
                username,
                name,
                email,
                org = orgDisplay,
            }
        );

        return Task.FromResult(0);
    }

    private static Dictionary<string, System.Text.Json.JsonElement> DecodeJwtPayload(string jwt) =>
        AuthService.DecodeJwtPayload(jwt);
}
