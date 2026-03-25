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

    /// <summary>
    /// Walks up the directory tree from <paramref name="startDirectory"/> to find the nearest
    /// <c>.bella</c> file. Returns its full path, or <c>null</c> if none is found.
    /// </summary>
    public static string? FindBellaFile(string startDirectory)
    {
        var dir = new DirectoryInfo(startDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, ".bella");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// Updates the <c>org</c> field in an existing <c>.bella</c> file at <paramref name="bellaFilePath"/>.
    /// If the file already has an <c>org</c> line it is replaced; otherwise <c>org = "..."</c> is
    /// inserted as the first line. Other lines are preserved as-is.
    /// </summary>
    public static void UpdateBellaOrg(string bellaFilePath, string orgSlug)
    {
        var lines = File.Exists(bellaFilePath)
            ? new System.Collections.Generic.List<string>(File.ReadAllLines(bellaFilePath))
            : [];

        var orgLine = $"org = \"{orgSlug}\"";
        var existingIndex = lines.FindIndex(l =>
            l.TrimStart().StartsWith("org", StringComparison.OrdinalIgnoreCase)
            && l.Contains('='));

        if (existingIndex >= 0)
            lines[existingIndex] = orgLine;
        else
            lines.Insert(0, orgLine);

        File.WriteAllText(bellaFilePath, string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }
}
