using System.Text.Json;
using BellaCli.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace BellaBaxter.Cli.Tests.Helpers;

/// <summary>
/// Writes <c>tokens.json</c> into an isolated HOME exactly as <see cref="CredentialStore"/> does (an encrypted blob under a key
/// ring in the same directory), so the REAL built <c>bella</c> run by <see cref="BellaProcess"/> finds a stored login there
/// without touching the real user's profile: the store's paths are static and read once, so it cannot be pointed at a
/// temporary home from inside the test process.
/// </summary>
public static class StoredLoginSeed
{
    /// <param name="home">The child's HOME (<see cref="BellaProcess.Home"/>).</param>
    /// <param name="serverUrl">The server the login belongs to (it is only ever presented there).</param>
    /// <param name="expiresAt">When the access token ends; in the past, the next command must refresh before it sends anything.</param>
    public static void Write(string home, string serverUrl, DateTimeOffset expiresAt)
    {
        var configDir = Path.Combine(home, ".config", "bella-cli");
        var keys = Path.Combine(configDir, "keys");
        Directory.CreateDirectory(keys);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(configDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(keys, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var services = new ServiceCollection();
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(keys))
            .SetApplicationName("bella-cli");
        var protector = services.BuildServiceProvider()
            .GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("bella-cli.credentials.v1");

        var tokens = new StoredTokens(
            AccessToken: "access-token-for-the-contract-test",
            RefreshToken: "refresh-token-for-the-contract-test",
            ExpiresAt: expiresAt,
            Origin: ConfigService.OriginOf(serverUrl));
        var json = JsonSerializer.Serialize(tokens, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        var file = Path.Combine(configDir, "tokens.json");
        File.WriteAllText(file, protector.Protect(json));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
