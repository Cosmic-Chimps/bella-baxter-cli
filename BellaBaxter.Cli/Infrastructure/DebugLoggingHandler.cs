namespace BellaCli.Infrastructure;

/// <summary>
/// DelegatingHandler that dumps every HTTP request/response to stderr when BELLA_BAXTER_DEBUG=1 —
/// header names always, a credential header's value never (<see cref="MaskedHeaders"/>).
/// Plug it into the HttpClient pipeline to diagnose auth and routing issues.
/// </summary>
public sealed class DebugLoggingHandler : DelegatingHandler
{
    /// <summary>
    /// Headers whose VALUE is a credential: printed as <c>***</c>, name kept (#1053). The same set as the Go
    /// SDK's <c>maskedHeaders</c> (<c>apps/sdk/go/bellabaxter/transport.go</c>); a test holds them equal.
    /// </summary>
    /// <remarks>
    /// This replaces a <c>TrackedRequestHeaders</c> list that named <c>Authorization</c> and
    /// <c>X-Bella-Signature</c> and was read by nothing, so every value went to stderr in clear. A timestamp,
    /// the E2E PUBLIC key and a role are not credentials and stay visible — they are what debug output is for.
    /// </remarks>
    internal static readonly IReadOnlySet<string> MaskedHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "X-Bella-Key-Id", "X-Bella-Signature", "Cookie", "Set-Cookie",
    };

    private static string Shown(string name, IEnumerable<string> values) =>
        MaskedHeaders.Contains(name) ? "***" : string.Join(", ", values);

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
        Console.Error.WriteLine($"[DEBUG] → {request.Method} {request.RequestUri}");

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
                Console.Error.WriteLine($"[DEBUG]   body: {body[..Math.Min(body.Length, 500)]}");
        }

        Console.ForegroundColor = color;
        return response;
    }
}
