using System.Text.Json;

namespace BellaCli.Services;

/// <summary>
/// Spec 108 (FR-018) — reads the API's 403 <c>organisation-setup-required</c> off a response. The sibling of
/// <see cref="PersonOnlyRefusal"/>, for the layer below Kiota: <see cref="TokenRefreshHandler"/> sees the raw response, on
/// every stored-person client, before any command's own error handling.
/// </summary>
internal static class OrganisationSetupRefusal
{
    /// <summary>The stable code the API sends as the ProblemDetails <c>type</c>.</summary>
    public const string Code = "organisation-setup-required";

    /// <summary>Longest body read when looking for the code: a refusal is a few hundred bytes, anything bigger is not one.</summary>
    private const long MaxBodyBytes = 16 * 1024;

    /// <summary>
    /// The exception for <paramref name="response"/> when it is that refusal; null for anything else. The body is buffered,
    /// so a response that is NOT this refusal can still be read by whoever receives it.
    /// </summary>
    public static async Task<OrganisationSetupRequiredException?> TryReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content is null)
            return null;

        try
        {
            await response.Content.LoadIntoBufferAsync(MaxBodyBytes, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !IsCode(Text(root, "type")))
                return null;

            return new OrganisationSetupRequiredException(Text(root, "detail"), Text(root, "setupUrl"));
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or InvalidOperationException)
        {
            // Not JSON, or larger than a refusal can be: some other 403, left exactly as it came.
            return null;
        }
    }

    /// <summary>The same match <see cref="PersonOnlyRefusal"/> uses: exact, or a URI that ends in the code.</summary>
    internal static bool IsCode(string? type) =>
        string.Equals(type, Code, StringComparison.Ordinal)
        || type?.EndsWith("/" + Code, StringComparison.Ordinal) == true;

    /// <summary>A string member, with control characters dropped: the text is printed to a terminal, and a body is not trusted with escape sequences.</summary>
    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? new string(value.GetString()!.Where(c => !char.IsControl(c)).ToArray())
            : null;
}
