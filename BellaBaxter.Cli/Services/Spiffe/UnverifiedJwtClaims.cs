using System.Text.Json;

namespace BellaCli.Services.Spiffe;

// Spec 081 T023 — the ONE local, UNVERIFIED decode of a node-evidence token, shared by `NodeEvidence` (Kubernetes) and
// `NomadEvidence`. Moved out of `NodeEvidence` without changing what it accepts: exactly three segments and a payload
// that decodes to a non-empty claim set. Nothing here checks a signature, an issuer or an expiry — the server does, at
// attestation — so a caller uses the result for DISPLAY and for the shape test "is this JWT-shaped, so it may be sent",
// never as proof of anything. The token itself is never kept.

/// <summary>Reads a JWT's claims locally, without verifying anything.</summary>
internal static class UnverifiedJwtClaims
{
    /// <summary>
    /// True when <paramref name="token"/> has three segments and a payload that decodes to at least one claim.
    /// </summary>
    public static bool TryRead(string token, out Dictionary<string, JsonElement> claims)
    {
        claims = [];
        if (token.Split('.').Length != 3)
            return false;
        try
        {
            claims = AuthService.DecodeJwtPayload(token);
            return claims.Count > 0;
        }
        catch
        {
            claims = [];
            return false;
        }
    }

    /// <summary>The <c>aud</c> claim as a list: a string is one audience, an array is its string members.</summary>
    public static IReadOnlyList<string> Audiences(Dictionary<string, JsonElement> claims)
    {
        if (!claims.TryGetValue("aud", out var aud))
            return [];
        return aud.ValueKind switch
        {
            JsonValueKind.String => [aud.GetString()!],
            JsonValueKind.Array => aud.EnumerateArray()
                .Where(a => a.ValueKind == JsonValueKind.String)
                .Select(a => a.GetString()!)
                .ToList(),
            _ => [],
        };
    }

    /// <summary>A string claim, or null when absent or not a string.</summary>
    public static string? Text(Dictionary<string, JsonElement> claims, string name) =>
        claims.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>A numeric-date claim (<c>exp</c>, <c>iat</c>), or null when absent or not a whole number.</summary>
    public static DateTimeOffset? Time(Dictionary<string, JsonElement> claims, string name) =>
        claims.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
}
