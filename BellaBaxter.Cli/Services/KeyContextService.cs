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
    /// Writes the API-key context into the <c>.bella</c> file in <paramref name="directory"/>,
    /// creating it when absent. This is a MERGE, not an overwrite: only <c>org</c>, <c>project</c>
    /// and <c>environment</c> are replaced; every other line — <c>url</c>, comments, keys this
    /// version does not know — is preserved in place.
    ///
    /// <para>Why: <c>url</c> is how the CLI knows WHICH deployment the context belongs to (see
    /// <see cref="ConfigService"/>). The previous overwrite dropped it on every API-key login, so the
    /// very next command went to the machine default server instead of the one the key was just
    /// validated against.</para>
    ///
    /// <para><paramref name="apiUrl"/> is recorded only when the file has no <c>url</c> yet, so an
    /// operator's explicit choice is never re-pointed by a login.</para>
    /// </summary>
    public static void WriteBellaFile(string directory, KeyContext ctx, string? apiUrl = null)
    {
        var path = Path.Combine(directory, ".bella");
        var lines = File.Exists(path)
            ? new List<string>(File.ReadAllLines(path))
            : [];

        // Insert in the conventional order (org, project, environment) so a fresh file reads
        // the same as one written by `bella context init`.
        if (ctx.OrgSlug is not null)
            UpsertLine(lines, "org", ctx.OrgSlug, insertAt: 0);
        var projectIndex = UpsertLine(lines, "project", ctx.ProjectSlug,
            insertAt: FindLine(lines, "org") is { } o ? o + 1 : 0);
        if (ctx.EnvironmentSlug is not null)
            UpsertLine(lines, "environment", ctx.EnvironmentSlug, insertAt: projectIndex + 1);
        else if (FindLine(lines, "environment") is { } e)
            lines.RemoveAt(e); // a project-scoped key has no environment; a stale one would mislead

        if (apiUrl is not null && FindLine(lines, "url") is null)
            lines.Add($"url = \"{apiUrl}\"");

        File.WriteAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }

    /// <summary>Index of the <c>key = …</c> line, or null. Comments and blank lines never match.</summary>
    private static int? FindLine(List<string> lines, string key)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var t = lines[i].TrimStart();
            if (t.StartsWith('#')) continue;
            var eq = t.IndexOf('=');
            if (eq < 0) continue;
            if (t[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return null;
    }

    /// <summary>Replaces the existing <c>key = …</c> line or inserts one at <paramref name="insertAt"/>; returns its index.</summary>
    private static int UpsertLine(List<string> lines, string key, string value, int insertAt)
    {
        var line = $"{key} = \"{value}\"";
        if (FindLine(lines, key) is { } i)
        {
            lines[i] = line;
            return i;
        }
        insertAt = Math.Clamp(insertAt, 0, lines.Count);
        lines.Insert(insertAt, line);
        return insertAt;
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
