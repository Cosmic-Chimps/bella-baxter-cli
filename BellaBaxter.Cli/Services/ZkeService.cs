using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BellaBaxter.Crypto;
using BellaCli.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace BellaCli.Services;

/// <summary>
/// Manages the per-device ZKE (Zero-Knowledge Encryption) identity keypair.
///
/// <para>The P-256 private key is generated once via <c>bella auth setup</c> and stored in an
/// encrypted file under <c>~/.config/bella-cli</c> with owner-only permissions. The encryption is
/// ASP.NET Data Protection with a file-system key ring in <c>keys/</c>; on Windows that ring is
/// DPAPI-protected, on Linux and macOS it is not (no <c>ProtectKeysWith*</c> is configured), which
/// is why the file permissions matter and why this class goes through
/// <see cref="PrivateFiles"/> — those permissions are the control, not a supporting measure
/// (an OS-protected key ring was considered and declined: backlog §2.23, closed).</para>
///
/// <para>The public key travels with each request as <c>X-E2E-Public-Key</c>. As of spec 037 it is also
/// REGISTERED: <c>bella auth setup</c> records it against the person and the tenant, and where the tenant
/// enforces ZKE the server admits only a key it can find in that registry. The sentence that used to sit
/// here — "there is no device registration step today" — was the defect stated as documentation: any
/// machine that generated a key satisfied the setting.</para>
///
/// <para>On reads, if the server returns <c>X-Bella-Wrapped-Dek</c>, the CLI decrypts it
/// with the private key to obtain the DEK, then decrypts any <c>bellabaxter:v1:</c>
/// prefixed values locally.</para>
/// </summary>
public class ZkeService
{
    private static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "bella-cli");

    private static readonly string PrivateKeyFile = Path.Combine(ConfigDir, "zke-private-key.dat");

    private readonly IDataProtector _protector;

    public ZkeService()
    {
        PrivateFiles.EnsurePrivateDirectory(ConfigDir);
        // Created by us, owner-only, BEFORE DataProtection creates it with the process umask.
        PrivateFiles.EnsurePrivateDirectory(Path.Combine(ConfigDir, "keys"));
        CredentialDirectory.TightenOnce(ConfigDir);

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
        PrivateFiles.WritePrivate(PrivateKeyFile, encrypted);

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
    /// <summary>
    /// Strips PEM armour to the base64 body, or returns raw base64 unchanged.
    /// </summary>
    /// <remarks>
    /// <para>#731 — this was written twice inline (for <c>file://</c> and for a bare path) and NOT at
    /// all for <c>env://</c>, which is the one <c>sdk run</c> writes into. So the moment the CLI
    /// started emitting PEM, its own <c>pull --private-key env://</c> would have stopped reading it —
    /// a fix for four SDKs that broke the tool shipping it.</para>
    /// <para>Handles CRLF as well as LF: a key that has been through a Windows editor, a CI variable
    /// or a copy-paste is still the same key, and failing on the line ending would present as
    /// "malformed key" for a file the operator can see is correct.</para>
    /// </remarks>
    internal static string StripPemArmour(string content)
    {
        var trimmed = content.Trim();

        if (!trimmed.StartsWith("-----BEGIN", StringComparison.Ordinal))
            return trimmed;

        return string.Concat(
            trimmed
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith("-----", StringComparison.Ordinal)));
    }

    public static string? ResolvePrivateKeyFromUrl(string url)
    {
        if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            var path = url["file://".Length..];
            return File.Exists(path) ? StripPemArmour(File.ReadAllText(path)) : null;
        }

        if (url.StartsWith("env://", StringComparison.OrdinalIgnoreCase))
        {
            var varName = url["env://".Length..];
            var value = Environment.GetEnvironmentVariable(varName);

            // #731 — the env var now normally CONTAINS PEM, because that is what `sdk run` injects
            // and what every non-.NET SDK reads. Both forms are accepted so a caller who exported
            // base64 by hand is not broken by the change.
            return value is null ? null : StripPemArmour(value);
        }

        // Bare path — treat as file
        if (File.Exists(url))
            return StripPemArmour(File.ReadAllText(url));

        return null;
    }

    /// <summary>
    /// The device private key as PKCS#8 PEM — the format the Python, JS, Go and Java SDKs read, and
    /// the one <c>docs/e2ee-zke.md</c> documents. Null when no device key exists.
    /// </summary>
    /// <remarks>
    /// #731 — <c>sdk run</c> used to inject <see cref="LoadPrivateKeyBase64"/> raw, so four of the
    /// five SDKs failed at client construction with a PEM framing error. Only the .NET SDK and the
    /// CLI's own <c>env://</c> reader accepted base64, which is why the July matrix missed it: that
    /// CLI version did not inject the key at all.
    /// </remarks>
    public string? LoadPrivateKeyPem()
    {
        var base64 = LoadPrivateKeyBase64();
        if (base64 is null)
            return null;

        try
        {
            // Round-trip through the key type rather than wrapping the base64 in a header by hand:
            // that would emit armour around whatever the file held, so a corrupt key would become a
            // well-formed PEM containing garbage, and the failure would surface inside somebody
            // else's SDK instead of here.
            using var ecdh = System.Security.Cryptography.ECDiffieHellman.Create();
            ecdh.ImportPkcs8PrivateKey(Convert.FromBase64String(base64), out _);
            return new string(System.Security.Cryptography.PemEncoding.Write(
                "PRIVATE KEY", ecdh.ExportPkcs8PrivateKey()));
        }
        catch
        {
            return null;
        }
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
