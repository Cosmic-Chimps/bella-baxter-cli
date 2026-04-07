using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace BellaCli.Services;

/// <summary>
/// Per-device cache for ECIES-wrapped Data Encryption Keys (DEKs) received from Bella API
/// via the <c>X-Bella-Wrapped-Dek</c> response header.
///
/// The wrapped DEK is already ECIES-encrypted with the device's public key — the server cannot
/// read it. We additionally DataProtect the file for defence-in-depth (OS-keyed encryption).
///
/// On a cache hit the caller decrypts the wrapped DEK locally with their private key,
/// uses it for the session, then discards the plaintext DEK — it is never written to disk.
/// </summary>
public class DekLeaseCache
{
    private static readonly string CacheDir =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "bella-cli", "dek-cache");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IDataProtector _protector;

    public DekLeaseCache()
    {
        Directory.CreateDirectory(CacheDir);

        var sp = new ServiceCollection()
            .AddDataProtection()
            .PersistKeysToFileSystem(
                new DirectoryInfo(
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".config", "bella-cli", "keys")))
            .SetApplicationName("bella-cli")
            .Services
            .BuildServiceProvider();

        _protector = sp.GetRequiredService<IDataProtectionProvider>()
                       .CreateProtector("bella-cli.dek-lease.v1");
    }

    /// <summary>
    /// Returns the cached wrapped-DEK string (base64 ECIES payload) for the given
    /// project/environment key, or null if the entry is absent or expired.
    /// </summary>
    public string? TryGet(string projectSlug, string envSlug)
    {
        var file = CacheFile(projectSlug, envSlug);
        if (!File.Exists(file)) return null;

        try
        {
            var ciphertext = File.ReadAllText(file);
            var json = _protector.Unprotect(ciphertext);
            var entry = JsonSerializer.Deserialize<CacheEntry>(json, Json);

            if (entry is null || entry.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                File.Delete(file);
                return null;
            }

            return entry.WrappedDek;
        }
        catch
        {
            // Corrupt / expired DataProtection key — evict
            try { File.Delete(file); } catch { /* ignore */ }
            return null;
        }
    }

    /// <summary>
    /// Caches the wrapped DEK for the given project/environment.
    /// <paramref name="expiresAt"/> defaults to 8 hours if null.
    /// </summary>
    public void Store(string projectSlug, string envSlug, string wrappedDek, DateTimeOffset? expiresAt = null)
    {
        var entry = new CacheEntry(
            wrappedDek,
            expiresAt ?? DateTimeOffset.UtcNow.AddHours(8));

        var json = JsonSerializer.Serialize(entry, Json);
        var ciphertext = _protector.Protect(json);
        File.WriteAllText(CacheFile(projectSlug, envSlug), ciphertext);
    }

    /// <summary>Removes all cached DEK leases (e.g. on logout or key rotation).</summary>
    public void Clear()
    {
        if (!Directory.Exists(CacheDir)) return;
        foreach (var f in Directory.GetFiles(CacheDir, "*.dat"))
        {
            try { File.Delete(f); } catch { /* ignore */ }
        }
    }

    /// <summary>Removes the cached DEK for one environment (e.g. after rotation).</summary>
    public void Evict(string projectSlug, string envSlug)
    {
        var file = CacheFile(projectSlug, envSlug);
        try { if (File.Exists(file)) File.Delete(file); } catch { /* ignore */ }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string CacheFile(string projectSlug, string envSlug)
    {
        // Sanitise slugs — only alphanumeric and hyphens allowed in path segments
        var safe = $"{Sanitise(projectSlug)}__{Sanitise(envSlug)}";
        return Path.Combine(CacheDir, $"{safe}.dat");
    }

    private static string Sanitise(string s) =>
        string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_'));

    private record CacheEntry(string WrappedDek, DateTimeOffset ExpiresAt);
}
