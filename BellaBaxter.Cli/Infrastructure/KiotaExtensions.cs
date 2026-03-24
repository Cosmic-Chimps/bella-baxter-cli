using Microsoft.Kiota.Abstractions.Serialization;

namespace BellaCli.Infrastructure;

/// <summary>
/// Helpers for reading values from Kiota's AdditionalData dictionaries.
/// Kiota 1.x stores JSON nested objects as UntypedObject (not JsonElement),
/// so standard JsonSerializer.Serialize won't produce the expected output.
/// </summary>
internal static class KiotaExtensions
{
    /// <summary>
    /// Converts an UntypedObject from Kiota's AdditionalData into a flat
    /// Dictionary&lt;string, string&gt;. Returns an empty dict on null or failure.
    /// </summary>
    public static Dictionary<string, string> ToStringDict(this object? raw)
    {
        if (raw is null) return new();

        try
        {
            if (raw is UntypedObject untypedObj)
                return untypedObj.GetValue().ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value is UntypedString s ? (s.GetValue() ?? "") : (kvp.Value?.ToString() ?? "")
                );

            // Fallback for older Kiota versions that may use JsonElement
            var json = System.Text.Json.JsonSerializer.Serialize(raw);
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
        }
        catch { return new(); }
    }

    /// <summary>
    /// Converts Kiota AdditionalData (UntypedString values) into a flat
    /// Dictionary&lt;string, string&gt;. Returns an empty dict on null.
    /// </summary>
    public static Dictionary<string, string> ToStringDict(this IDictionary<string, object>? additionalData)
    {
        if (additionalData is null) return new();
        return additionalData.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value is UntypedString s ? (s.GetValue() ?? "") : (kvp.Value?.ToString() ?? "")
        );
    }
}
