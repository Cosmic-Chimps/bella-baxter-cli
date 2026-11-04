using BellaBaxter.Client;
using BellaBaxter.Client.Models;
using BellaCli.Infrastructure;

namespace BellaCli.Commands.Run;

/// <summary>
/// What <c>bella run --provider &lt;slug&gt;</c> injects (#1616): the project's global secrets, then that one
/// provider's secrets on top, key by key.
///
/// <para><c>--provider</c> was declared on <c>run</c> and never read: every run called the bulk environment
/// endpoint, which merges EVERY provider of the environment (last one wins on a key collision), so the child
/// received secrets the operator had asked to leave out. With the option, <c>run</c> now reads the way
/// <c>bella secrets get --provider</c> does — the provider-scoped route, through <see cref="ProviderSecretValues"/>.</para>
///
/// <para>The globals come along because they are a PROJECT layer, not a provider: the bulk route seeds them first
/// and lets the environment's providers override them (UC-PROJ-04), and a <c>run --provider</c> that dropped them
/// would silently take configuration away from an app that had it without the option. Same precedence here: the
/// provider wins a name collision. The server applies the token's scope on both routes itself
/// (<c>SecretScopeFilter</c>), so a scoped token sees the same set through either; nothing is filtered here.</para>
/// </summary>
internal static class RunSecretSources
{
    public static async Task<Dictionary<string, string>> ReadProviderAsync(
        BellaClient client,
        string project,
        string environment,
        string provider,
        CancellationToken ct)
    {
        var globals = await client.Api.V1.Projects[project].Secrets.GetAsync(cancellationToken: ct);
        // Null is "could not read", not "no secrets" (an empty provider is an empty map). Starting the child with the
        // globals alone would hand it a different configuration and say nothing, so it is a failure: RunCommand
        // reports it and exits 1 before the child starts (Security's review of #1630).
        var provided =
            await ProviderSecretValues.ReadAsync(client, project, environment, provider, ct)
            ?? throw new InvalidOperationException(
                $"the server answered with no body for provider '{provider}', so its secrets could not be read.");

        return Merge(globals?.Secrets, provided);
    }

    /// <summary>Globals first, the provider's values over them; a null global value is an empty string.</summary>
    public static Dictionary<string, string> Merge(
        IEnumerable<GlobalSecretItem>? globals,
        IReadOnlyDictionary<string, string> provider)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var global in globals ?? [])
        {
            if (global.Key is { } key)
                merged[key] = global.Value ?? "";
        }

        foreach (var (key, value) in provider)
            merged[key] = value;

        return merged;
    }
}
