namespace BellaCli.Services;

/// <summary>
/// Calls <c>GET /api/v1/keys/me</c> to discover the project+environment context
/// embedded in the currently-stored API key.
///
/// This is used to automatically write (or overwrite) the <c>.bella</c> file whenever
/// the user logs in with an API key or runs <c>bella context init</c> with an API key.
/// </summary>
public class KeyContextService(BellaClientProvider provider, CredentialStore credentials)
{
    public record KeyContext(
        string ProjectSlug,
        string ProjectName,
        string? EnvironmentSlug,
        string? EnvironmentName,
        string Role,
        string? OrgSlug = null,
        string? OrgName = null
    );

    /// <summary>
    /// Discovers the project/environment context from the stored API key by calling
    /// <c>GET /api/v1/keys/me</c>. Returns null if not in API key mode or on any error.
    /// </summary>
    public async Task<KeyContext?> DiscoverAsync(CancellationToken ct = default)
    {
        var apiKey = credentials.LoadApiKey();
        if (apiKey is null)
            return null;

        try
        {
            var client = provider.CreateClient();
            var response = await client.Api.V1.Keys.Me.GetAsync(cancellationToken: ct);

            if (response?.ProjectSlug is null)
                return null;

            // TenantSlug / TenantName were added after the SDK was generated.
            // Read from AdditionalData (Kiota stores unknown fields there) until next SDK regen.
            var orgSlug = TryGetAdditionalString(response.AdditionalData, "tenantSlug");
            var orgName = TryGetAdditionalString(response.AdditionalData, "tenantName");

            return new KeyContext(
                response.ProjectSlug,
                response.ProjectName ?? response.ProjectSlug,
                response.EnvironmentSlug,
                response.EnvironmentName ?? response.EnvironmentSlug,
                response.Role ?? "CONSUMER",
                OrgSlug: orgSlug,
                OrgName: orgName
            );
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetAdditionalString(IDictionary<string, object>? data, string key)
    {
        if (data is null || !data.TryGetValue(key, out var val)) return null;
        return val?.ToString();
    }

    /// <summary>
    /// Writes (or overwrites) a <c>.bella</c> file in <paramref name="directory"/>
    /// with the project, environment, and org (when available) from <paramref name="ctx"/>.
    /// </summary>
    public static void WriteBellaFile(string directory, KeyContext ctx)
    {
        var path = Path.Combine(directory, ".bella");
        var sb = new System.Text.StringBuilder();
        if (ctx.OrgSlug is not null)
            sb.AppendLine($"org = \"{ctx.OrgSlug}\"");
        sb.AppendLine($"project = \"{ctx.ProjectSlug}\"");
        if (ctx.EnvironmentSlug is not null)
            sb.AppendLine($"environment = \"{ctx.EnvironmentSlug}\"");
        File.WriteAllText(path, sb.ToString());
    }
}
