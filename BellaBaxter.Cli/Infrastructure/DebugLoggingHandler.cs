using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BellaCli.Infrastructure;

/// <summary>
/// DelegatingHandler that dumps every HTTP request/response to stderr when BELLA_BAXTER_DEBUG=1 —
/// header names always, a key- or credential-bearing header's value never (<see cref="IsSensitiveHeader"/>), and a
/// failure body only with its key-bearing fields masked (<see cref="RedactBody"/>); a URL it prints has the value of every
/// credential-named query parameter and its userinfo masked (<see cref="MaskUrl"/>).
/// Plug it into the HttpClient pipeline to diagnose auth and routing issues.
/// </summary>
public sealed class DebugLoggingHandler : DelegatingHandler
{
    /// <summary>
    /// Headers whose VALUE is a credential or key material: printed as <c>***</c>, name kept (#1053, #2002). The first five
    /// are the Go SDK's <c>maskedHeaders</c> (<c>apps/sdk/go/bellabaxter/transport.go</c>); a test holds the Go set to be a
    /// subset of what is masked here. The rest are every key-bearing header the CLI, the SDKs and the API read or write:
    /// the wrapped data keys (<c>X-Bella-Wrapped-Dek</c>, <c>X-Bella-Wrapped-Shared-Dek</c>), the DEK lease id, the call proof,
    /// the invoke keys of the cloud functions and the vault and cloud-metadata tokens.
    /// </summary>
    /// <remarks>
    /// This replaces a <c>TrackedRequestHeaders</c> list that named <c>Authorization</c> and
    /// <c>X-Bella-Signature</c> and was read by nothing, so every value went to stderr in clear. A timestamp,
    /// the E2E PUBLIC key and a role are not credentials and stay visible — they are what debug output is for.
    /// </remarks>
    internal static readonly IReadOnlySet<string> MaskedHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "X-Bella-Key-Id", "X-Bella-Signature", "Cookie", "Set-Cookie",
        "X-Bella-Wrapped-Dek", "X-Bella-Wrapped-Shared-Dek", "X-Bella-Lease-Id", "X-Bella-Call-Proof",
        "X-Bella-Api-Key", "X-Api-Key", "X-Secret-Key", "x-functions-key", "DD-API-KEY",
        "X-Vault-Token", "X-aws-ec2-metadata-token", "X-IDENTITY-HEADER", "Stripe-Signature",
        "Proxy-Authorization",
    };

    /// <summary>
    /// A header whose NAME contains one of these is masked even when it is on no list, so a key-bearing header added tomorrow
    /// is hidden by default (#2002). Matched case-insensitively against the name as written.
    /// </summary>
    internal static readonly string[] SensitiveNameParts =
    [
        "key", "token", "secret", "dek", "signature", "cookie", "authorization", "wrapped", "proof", "password", "credential",
    ];

    /// <summary>
    /// What the rule would catch that is NOT secret and is what debug output is for: the E2E PUBLIC key the client sends so
    /// the server can wrap a data key to it. The only exception.
    /// </summary>
    internal static readonly IReadOnlySet<string> NeverMasked = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "X-E2E-Public-Key",
    };

    /// <summary>True when the value of the header called <paramref name="name"/> must never be printed (list or rule).</summary>
    internal static bool IsSensitiveHeader(string name)
    {
        if (NeverMasked.Contains(name))
            return false;
        if (MaskedHeaders.Contains(name))
            return true;
        foreach (var part in SensitiveNameParts)
        {
            if (name.Contains(part, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // Headers that carry a URL, which can carry a credential in its query.
    private static readonly IReadOnlySet<string> UrlHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Location", "Content-Location", "Referer",
    };

    private static string Shown(string name, IEnumerable<string> values)
    {
        if (IsSensitiveHeader(name))
            return "***";
        return UrlHeaders.Contains(name)
            ? string.Join(", ", values.Select(MaskUrl))
            : string.Join(", ", values);
    }

    /// <summary>
    /// Query parameters the name rule misses. <c>sig</c> is the signature of an Azure SAS URL; the rest of a SAS
    /// (<c>se</c>, <c>sv</c>, <c>sp</c>: expiry, version, permissions) is harmless and is what a person debugging an
    /// expired link needs to read. The presigned S3 names are all caught by the rule itself
    /// (<c>X-Amz-Signature</c>, <c>X-Amz-Credential</c>, <c>X-Amz-Security-Token</c>). The Go SDK holds the same set.
    /// </summary>
    internal static readonly IReadOnlySet<string> SensitiveQueryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "sig",
    };

    private static readonly Regex AuthorityPrefix = new(
        @"^(?:[A-Za-z][A-Za-z0-9+.-]*:)?//", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static bool IsSensitiveQueryName(string name)
    {
        name = Uri.UnescapeDataString(name.Replace('+', ' '));
        return SensitiveQueryNames.Contains(name) || IsSensitiveHeaderRule(name);
    }

    private static bool IsSensitiveHeaderRule(string name) =>
        SensitiveNameParts.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A URL for the debug output: the value of every query parameter whose NAME is sensitive (the header rule, plus
    /// <c>sig</c>) and the userinfo become <c>***</c>. Scheme, host, port, path, fragment, the other parameters, their order and
    /// repeated names are kept; a URL with neither comes back unchanged. It works on the text, so a string that does not
    /// parse is masked the same way. Every place the logger prints a URL uses it.
    /// </summary>
    internal static string MaskUrl(string url)
    {
        var rest = url;
        var fragment = string.Empty;
        var hash = rest.IndexOf('#');
        if (hash >= 0)
        {
            fragment = rest[hash..];
            rest = rest[..hash];
        }

        string? query = null;
        var question = rest.IndexOf('?');
        if (question >= 0)
        {
            query = rest[(question + 1)..];
            rest = rest[..question];
        }

        var prefix = AuthorityPrefix.Match(rest);
        if (prefix.Success)
        {
            var authorityStart = prefix.Length;
            var slash = rest.IndexOf('/', authorityStart);
            var authorityEnd = slash >= 0 ? slash : rest.Length;
            var at = rest.LastIndexOf('@', authorityEnd - 1, authorityEnd - authorityStart);
            if (at >= 0)
                rest = string.Concat(rest.AsSpan(0, authorityStart), "***@", rest.AsSpan(at + 1));
        }

        if (query is null)
            return rest + fragment;

        var pairs = query.Split('&');
        for (var i = 0; i < pairs.Length; i++)
        {
            var eq = pairs[i].IndexOf('=');
            if (eq >= 0 && IsSensitiveQueryName(pairs[i][..eq]))
                pairs[i] = pairs[i][..eq] + "=***";
        }
        return rest + "?" + string.Join('&', pairs) + fragment;
    }

    /// <summary>
    /// The first <paramref name="max"/> characters of <paramref name="text"/>, cut on a character boundary: a surrogate pair
    /// (an emoji) that straddles the limit is dropped whole, so the output never ends on a lone high surrogate.
    /// </summary>
    internal static string Truncate(string text, int max)
    {
        if (text.Length <= max)
            return text;
        var cut = max;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
            cut--;
        return text[..cut];
    }

    private static bool IsSensitiveField(string name)
    {
        // snake_case, kebab-case and camelCase reduce to the same letters: wrapped_dek, wrappedDek, wrapped-dek.
        var flat = name.Replace("_", string.Empty).Replace("-", string.Empty);
        foreach (var part in SensitiveNameParts)
        {
            if (flat.Contains(part, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // A text body that is not JSON (a proxy's page, a form echo): name=value or name: value pairs with a sensitive name.
    private static readonly Regex SensitivePair = new(
        @"(?<name>[\w-]*(?:key|token|secret|dek|signature|password|wrapped|proof|credential)[\w-]*)(?<sep>\s*[=:]\s*)(?:""[^""]*""|[^\s&,;""]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>
    /// A failure body for the debug output with every key-bearing field masked. A JSON body (the API's problem document, an
    /// OAuth error, a DEK lease or a token response) keeps its shape and loses the value of every property whose name matches
    /// <see cref="SensitiveNameParts"/> (whatever type the value is); anything else gets its <c>name=value</c> pairs masked the
    /// same way.
    /// </summary>
    internal static string RedactBody(string body)
    {
        try
        {
            if (JsonNode.Parse(body) is { } node)
            {
                Redact(node);
                return node.ToJsonString();
            }
        }
        catch (JsonException)
        {
            // not JSON: fall through to the pair mask
        }
        return SensitivePair.Replace(body, m => $"{m.Groups["name"].Value}{m.Groups["sep"].Value}***");
    }

    private static void Redact(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    if (IsSensitiveField(key))
                        obj[key] = "***";
                    else if (obj[key] is { } child)
                        Redact(child);
                }
                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    if (child is not null)
                        Redact(child);
                }
                break;
        }
    }

    public static bool IsEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable("BELLA_BAXTER_DEBUG")
            ?? Environment.GetEnvironmentVariable("BELLA_DEBUG"), // deprecated
            "1",
            StringComparison.Ordinal);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!IsEnabled)
            return await base.SendAsync(request, cancellationToken);

        var color = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.Error.WriteLine($"[DEBUG] → {request.Method} {MaskUrl(request.RequestUri?.OriginalString ?? string.Empty)}");

        // Execute the pipeline first — inner handlers (HmacSigningHandler, E2EEncryptionHandler)
        // mutate the HttpRequestMessage in-place before sending, so we read headers AFTER.
        var response = await base.SendAsync(request, cancellationToken);

        // Log all request headers as actually sent
        Console.Error.WriteLine("[DEBUG]   request headers sent:");
        foreach (var h in request.Headers)
            Console.Error.WriteLine($"[DEBUG]     {h.Key}: {Shown(h.Key, h.Value)}");

        Console.Error.WriteLine($"[DEBUG] ← {(int)response.StatusCode} {response.ReasonPhrase}");
        foreach (var h in response.Headers)
            Console.Error.WriteLine($"[DEBUG]   {h.Key}: {Shown(h.Key, h.Value)}");

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(body))
            {
                // A failure body can echo a token or a wrapped key (an OAuth error, a lease): mask those fields BEFORE the cut.
                var shown = RedactBody(body);
                Console.Error.WriteLine($"[DEBUG]   body: {Truncate(shown, 500)}");
            }
        }

        Console.ForegroundColor = color;
        return response;
    }
}
