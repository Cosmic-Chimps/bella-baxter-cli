using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BellaBaxter.Crypto;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace BellaCli.Services;

/// <summary>
/// Manages the per-device ZKE (Zero-Knowledge Encryption) identity keypair.
///
/// The P-256 private key is generated once via <c>bella auth setup</c> and persisted
/// on disk using ASP.NET Data Protection (OS-keyed: macOS Keychain / Windows DPAPI /
/// Linux file ACL). The public key is registered with Bella API so the server can
/// wrap project DEKs with it on every secret pull.
///
/// On reads, if the server returns <c>X-Bella-Wrapped-Dek</c>, the CLI decrypts it
/// with the private key to obtain the DEK, then decrypts any <c>bellabaxter:v1:</c>
/// prefixed values locally — zero-knowledge on the read path.
/// </summary>
public class ZkeService
{
    private static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "bella-cli");

    private static readonly string PrivateKeyFile = Path.Combine(ConfigDir, "zke-private-key.dat");

    private readonly IDataProtector _protector;

    public ZkeService()
    {
        Directory.CreateDirectory(ConfigDir);

        var services = new ServiceCollection();
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(ConfigDir, "keys")))
            .SetApplicationName("bella-cli");

        var provider = services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
        _protector = provider.CreateProtector("bella-cli.zke.v1");
    }

    // ── Keypair management ───────────────────────────────────────────────────

    /// <summary>
    /// Loads the stored private key as a base64-encoded PKCS#8 string,
    /// suitable for injecting as the <c>BELLA_BAXTER_PRIVATE_KEY</c> environment variable
    /// into subprocess environments (e.g. <c>bella exec</c>).
    /// Returns null if no keypair is set up or if loading fails.
    /// </summary>
    public string? LoadPrivateKeyBase64()
    {
        if (!File.Exists(PrivateKeyFile)) return null;
        try
        {
            var encrypted = File.ReadAllText(PrivateKeyFile);
            return _protector.Unprotect(encrypted); // already base64 PKCS#8
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Returns true if a device ZKE keypair is already set up.</summary>
    public bool HasKeypair() => File.Exists(PrivateKeyFile);

    /// <summary>
    /// Loads the stored private key as an <see cref="ECDiffieHellman"/> object for use in
    /// <see cref="BellaBaxter.Client.ZkeDekHandler"/>. The caller is responsible for disposing.
    /// Returns null if no keypair is set up or if loading fails.
    /// </summary>
    public ECDiffieHellman? LoadEcdhKey()
    {
        if (!File.Exists(PrivateKeyFile)) return null;
        try
        {
            var encrypted = File.ReadAllText(PrivateKeyFile);
            var pkcs8 = Convert.FromBase64String(_protector.Unprotect(encrypted));
            var ecdh = ECDiffieHellman.Create();
            ecdh.ImportPkcs8PrivateKey(pkcs8, out _);
            return ecdh;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Generates a new P-256 keypair for this device, persists the private key
    /// (encrypted with DataProtection), and returns the public key as base64 SPKI
    /// for registration with Bella API.
    /// </summary>
    public string GenerateAndSaveKeypair()
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        // Save private key (PKCS#8 format, DataProtection encrypted)
        var pkcs8 = ecdh.ExportPkcs8PrivateKey();
        var encrypted = _protector.Protect(Convert.ToBase64String(pkcs8));
        File.WriteAllText(PrivateKeyFile, encrypted);

        // Return public key as base64 SPKI for API registration
        var spki = ecdh.ExportSubjectPublicKeyInfo();
        return Convert.ToBase64String(spki);
    }

    /// <summary>
    /// Returns the public key (base64 SPKI) of the device keypair, or null if not set up.
    /// </summary>
    public string? GetPublicKey()
    {
        if (!File.Exists(PrivateKeyFile)) return null;

        try
        {
            var encrypted = File.ReadAllText(PrivateKeyFile);
            var pkcs8 = Convert.FromBase64String(_protector.Unprotect(encrypted));
            using var ecdh = ECDiffieHellman.Create();
            ecdh.ImportPkcs8PrivateKey(pkcs8, out _);
            return Convert.ToBase64String(ecdh.ExportSubjectPublicKeyInfo());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Deletes the device keypair. Use with caution.</summary>
    public void DeleteKeypair()
    {
        if (File.Exists(PrivateKeyFile))
            File.Delete(PrivateKeyFile);
    }

    // ── DEK operations ────────────────────────────────────────────────────────

    /// <summary>
    /// Decrypts a wrapped DEK received in <c>X-Bella-Wrapped-Dek</c> response header.
    /// The header value is base64(JSON(E2EEncryptedPayload)).
    /// Returns the raw DEK bytes, or null if decryption fails.
    /// </summary>
    public byte[]? DecryptWrappedDek(string wrappedDekBase64Header)
    {
        if (!File.Exists(PrivateKeyFile)) return null;

        try
        {
            var encrypted = File.ReadAllText(PrivateKeyFile);
            var pkcs8 = Convert.FromBase64String(_protector.Unprotect(encrypted));
            using var ecdh = ECDiffieHellman.Create();
            ecdh.ImportPkcs8PrivateKey(pkcs8, out _);

            // Decode header: base64(UTF8 JSON of E2EEncryptedPayload)
            var wrappedDekJson = Encoding.UTF8.GetString(Convert.FromBase64String(wrappedDekBase64Header));
            var payload = JsonSerializer.Deserialize<E2EEncryptedPayload>(wrappedDekJson);
            if (payload is null) return null;

            return EciesAlgorithm.Decrypt(payload, ecdh);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Decrypts a wrapped DEK using an explicit private key (from --private-key flag).
    /// Supports schemes: file://, env://.
    /// </summary>
    public static byte[]? DecryptWrappedDekWithPrivateKey(string wrappedDekBase64Header, string privateKeyUrl)
    {
        try
        {
            var privateKeyBase64 = ResolvePrivateKeyFromUrl(privateKeyUrl);
            if (privateKeyBase64 is null) return null;

            var wrappedDekJson = Encoding.UTF8.GetString(Convert.FromBase64String(wrappedDekBase64Header));
            var payload = JsonSerializer.Deserialize<E2EEncryptedPayload>(wrappedDekJson);
            if (payload is null) return null;

            using var ecdh = ECDiffieHellman.Create();
            ecdh.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyBase64), out _);
            return EciesAlgorithm.Decrypt(payload, ecdh);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Decrypts a single <c>bellabaxter:v1:</c> prefixed value using the given DEK.
    /// Returns the original value unchanged if it's not encrypted or decryption fails.
    /// </summary>
    public static string DecryptWithDek(string value, byte[] dek)
    {
        if (!DekAlgorithm.IsEncrypted(value))
            return value;

        try
        {
            return DekAlgorithm.DecryptToString(value, dek);
        }
        catch
        {
            return value;
        }
    }

    /// <summary>
    /// Decrypts all <c>bellabaxter:v1:</c> prefixed values in a secrets dictionary.
    /// Returns the same dictionary with values replaced by plaintext.
    /// </summary>
    public static Dictionary<string, string> DecryptAllWithDek(
        Dictionary<string, string> secrets,
        byte[] dek)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in secrets)
            result[k] = DecryptWithDek(v, dek);
        return result;
    }

    // ── Private key URL resolver ──────────────────────────────────────────────

    /// <summary>
    /// Resolves a private key from a URL. Returns base64-encoded PKCS#8 private key.
    ///
    /// Supported schemes:
    ///   file:///path/to/key.pem   — PEM or raw PKCS#8 base64 file
    ///   env://VARIABLE_NAME       — read PKCS#8 base64 from environment variable
    ///
    /// Future: aws-kms://, vault://, azure-kv://
    /// </summary>
    public static string? ResolvePrivateKeyFromUrl(string url)
    {
        if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            var path = url["file://".Length..];
            if (!File.Exists(path)) return null;

            var content = File.ReadAllText(path).Trim();

            // Handle PEM format
            if (content.StartsWith("-----BEGIN"))
            {
                var lines = content.Split('\n');
                var b64 = string.Concat(lines
                    .Where(l => !l.StartsWith("-----"))
                    .Select(l => l.Trim()));
                return b64;
            }

            // Assume raw base64 PKCS#8
            return content;
        }

        if (url.StartsWith("env://", StringComparison.OrdinalIgnoreCase))
        {
            var varName = url["env://".Length..];
            return Environment.GetEnvironmentVariable(varName);
        }

        // Bare path — treat as file
        if (File.Exists(url))
        {
            var content = File.ReadAllText(url).Trim();
            if (content.StartsWith("-----BEGIN"))
            {
                var lines = content.Split('\n');
                return string.Concat(lines
                    .Where(l => !l.StartsWith("-----"))
                    .Select(l => l.Trim()));
            }
            return content;
        }

        return null;
    }

    /// <summary>
    /// Derives the SPKI base64 public key from a private key URL.
    /// Used in M2M flows to send <c>X-E2E-Public-Key</c> so the server can wrap the DEK.
    /// Returns null if the URL cannot be resolved or the key is invalid.
    /// </summary>
    public static string? GetPublicKeyFromPrivateKeyUrl(string privateKeyUrl)
    {
        try
        {
            var pkcs8b64 = ResolvePrivateKeyFromUrl(privateKeyUrl);
            if (pkcs8b64 is null) return null;

            using var ecdh = ECDiffieHellman.Create();
            ecdh.ImportPkcs8PrivateKey(Convert.FromBase64String(pkcs8b64), out _);
            return Convert.ToBase64String(ecdh.ExportSubjectPublicKeyInfo());
        }
        catch
        {
            return null;
        }
    }
}
