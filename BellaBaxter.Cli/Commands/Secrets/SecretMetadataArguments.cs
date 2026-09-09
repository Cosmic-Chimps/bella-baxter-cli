using BellaBaxter.Client.Models;

namespace BellaCli.Commands.Secrets;

/// <summary>
/// The one place that turns <c>--scope</c> / <c>--tag</c> arguments into the tag dictionary the
/// server actually reads, and the one place that validates them.
///
/// <para><b>Why this is not a generic key/value passthrough (issue #611).</b> "Scope tags" are not
/// arbitrary tags. Both enforcement sites read ONE entry whose key is literally <c>scope</c> and
/// whose value is a SPACE-SEPARATED list —
/// <c>Features/Projects/Environments/Secrets/GetSecret.cs</c> does
/// <c>Tags.GetValueOrDefault("scope").Split(' ')</c>, and <c>GetSecretsByProvider.cs</c> filters the
/// same way. The console writes exactly that shape
/// (<c>projects/detail/SecretsTab.vue</c>: <c>{ scope: scopes.join(" ") }</c>).</para>
///
/// <para>So the obvious reading of "let the CLI set tags" — <c>--tag drive</c> becoming
/// <c>{"drive":"true"}</c> — would persist a tag no token can ever match, and the operator would see
/// a tag in the console and a token that reads nothing. That is the pilot's F2 failure again: a
/// silent contract mismatch that looks like working code.</para>
/// </summary>
public static class SecretMetadataArguments
{
    /// <summary>The tag key both enforcement sites read. Not configurable — the server hard-codes it.</summary>
    public const string ScopeTagKey = "scope";

    /// <summary>
    /// Validates the raw arguments and composes the tag entries this command intends to write.
    /// Returns false with a message on the first problem; no partial result is produced, because a
    /// half-applied set of tags is worse than none.
    /// </summary>
    /// <param name="scopes">Values of repeated <c>--scope</c>.</param>
    /// <param name="tags">Values of repeated <c>--tag</c>, each <c>KEY=VALUE</c>.</param>
    /// <param name="composed">The entries to write, BEFORE merging with what the secret already has.</param>
    public static bool TryCompose(
        IReadOnlyList<string> scopes,
        IReadOnlyList<string> tags,
        out Dictionary<string, string> composed,
        out string? error
    )
    {
        composed = [];
        error = null;

        foreach (var scope in scopes)
        {
            if (string.IsNullOrWhiteSpace(scope))
            {
                error = "A --scope value cannot be empty.";
                return false;
            }

            // A space is the SEPARATOR in the stored value, so a scope containing whitespace would
            // silently become two scopes — and the operator would never see which.
            if (scope.Any(char.IsWhiteSpace))
            {
                error =
                    $"Invalid scope '{scope}': scopes cannot contain whitespace "
                    + "(scopes are stored space-separated, so it would split into two). "
                    + "Pass --scope once per scope.";
                return false;
            }
        }

        foreach (var raw in tags)
        {
            var separator = raw.IndexOf('=');
            if (separator <= 0)
            {
                error = $"Invalid tag '{raw}'. Tags are KEY=VALUE, e.g. --tag owner=platform.";
                return false;
            }

            var key = raw[..separator].Trim();
            var value = raw[(separator + 1)..];

            if (key.Length == 0)
            {
                error = $"Invalid tag '{raw}': the key is empty.";
                return false;
            }

            if (key.Equals(ScopeTagKey, StringComparison.OrdinalIgnoreCase) && scopes.Count > 0)
            {
                // Two ways to write one thing, disagreeing. Refuse rather than pick a winner.
                error =
                    "Pass either --scope or --tag scope=…, not both — they write the same entry. "
                    + "Prefer --scope; it is validated and matches 'bella issue --scope'.";
                return false;
            }

            if (composed.ContainsKey(key))
            {
                error = $"Tag '{key}' was given more than once.";
                return false;
            }

            composed[key] = value;
        }

        if (scopes.Count > 0)
        {
            // The shape the server reads: one entry, space-separated, in the order given.
            composed[ScopeTagKey] = string.Join(' ', scopes);
        }

        return true;
    }

    /// <summary>
    /// Merges the composed entries over the secret's existing tags.
    ///
    /// <para><b>Why merging is not optional.</b> <c>SecretTagsUpdated</c> REPLACES the whole
    /// dictionary (<c>SecretProjection.Apply</c>: <c>view.Tags = evt.Tags</c>), so sending only
    /// <c>scope</c> would delete every other tag the secret carries — <c>bella:env-specific</c>
    /// among them, which excludes a secret from drift. The console learned this the hard way and its
    /// own comment records it: a hardcoded <c>{}</c> "wiped every tag on the secret … a scoped token
    /// then lost access to it".</para>
    ///
    /// <para>Existing entries the caller did not mention are carried through; entries the caller
    /// gave win. Nothing is ever removed by omission.</para>
    /// </summary>
    public static Dictionary<string, string> MergeOver(
        IReadOnlyDictionary<string, string>? existing,
        IReadOnlyDictionary<string, string> composed
    )
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);

        if (existing is not null)
        {
            foreach (var (k, v) in existing)
                merged[k] = v;
        }

        foreach (var (k, v) in composed)
            merged[k] = v;

        return merged;
    }

    /// <summary>Builds the request body. Every unspecified member stays null — the endpoint documents null as "don't touch".</summary>
    public static UpdateSecretMetadataRequest BuildRequest(
        IReadOnlyDictionary<string, string>? tags,
        bool? ignoreInScan
    )
    {
        var request = new UpdateSecretMetadataRequest { IgnoreInScan = ignoreInScan };

        if (tags is not null)
        {
            var container = new UpdateSecretMetadataRequest_tags();
            foreach (var (k, v) in tags)
                container.AdditionalData[k] = v;
            request.Tags = container;
        }

        return request;
    }
}
