using BellaBaxter.Client;
using BellaBaxter.Client.Models;

namespace BellaCli.Infrastructure;

/// <summary>
/// The ONE reader of a provider's secrets for the commands that need a key → value map (#1187): <c>bella secrets get
/// --provider</c>, <c>bella secrets list</c> and <c>bella agent</c>.
/// </summary>
/// <remarks>
/// <para>It reads the typed <c>listSecrets</c> response, <c>List&lt;SecretItem&gt;</c>. That is what the server returns
/// without an E2EE key and what the SDK's handlers hand on after opening the envelope, and the OpenAPI document now
/// declares exactly that. The three commands used to read <c>AdditionalData["secrets"]</c> instead, because the document
/// declared the envelope as the body (so the typed model was empty) and the .NET SDK rewrote every decrypted item list into
/// <c>{"secrets":{"KEY":"VALUE"},"version":0}</c> for them. That rewrite is gone.</para>
/// </remarks>
internal static class ProviderSecretValues
{
    /// <summary>
    /// <paramref name="provider"/>'s secrets as key → value, or <c>null</c> when the server answered with no body (a
    /// provider with no secrets is an EMPTY map, not <c>null</c>, so "could not read" stays distinguishable from "none").
    /// </summary>
    public static async Task<Dictionary<string, string>?> ReadAsync(
        BellaClient client,
        string project,
        string environment,
        string provider,
        CancellationToken ct)
    {
        var items = await client
            .Api.V1.Projects[project]
            .Environments[environment]
            .Providers[provider]
            .Secrets.GetAsync(cancellationToken: ct);

        return items is null ? null : items.ToValueMap();
    }

    /// <summary>Key → value for each item; a null value is an empty string, and a repeated key takes the last.</summary>
    public static Dictionary<string, string> ToValueMap(this IEnumerable<SecretItem>? items)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in items ?? [])
        {
            if (item.Key is { } key)
                map[key] = item.Value ?? "";
        }

        return map;
    }
}
