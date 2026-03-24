namespace BellaCli.Infrastructure;

/// <summary>
/// DelegatingHandler that dumps every HTTP request/response to stderr when BELLA_DEBUG=1.
/// Plug it into the HttpClient pipeline to diagnose auth and routing issues.
/// </summary>
public sealed class DebugLoggingHandler : DelegatingHandler
{
    private static readonly string[] TrackedRequestHeaders =
    [
        "X-Bella-Key-Id", "X-Bella-Timestamp", "X-Bella-Signature",
        "X-E2E-Public-Key", "Authorization",
    ];

    private static readonly string[] TrackedResponseHeaders =
    [
        "X-User-Role", "WWW-Authenticate", "Content-Type",
    ];

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
            Console.Error.WriteLine($"[DEBUG]     {h.Key}: {string.Join(", ", h.Value)}");

        Console.Error.WriteLine($"[DEBUG] ← {(int)response.StatusCode} {response.ReasonPhrase}");
        foreach (var h in response.Headers)
            Console.Error.WriteLine($"[DEBUG]   {h.Key}: {string.Join(", ", h.Value)}");

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
