using System.Security.Cryptography;

namespace BellaCli.Commands.Issue;

/// <summary>
/// spec 040 US4 — produces the keypair a CI runner uses to satisfy ZKE enforcement, and reads a public
/// key an operator already has.
/// </summary>
/// <remarks>
/// <para><b>The private half is never written into the CLI's own config directory, and that is the
/// whole point of this type existing.</b> Backlog §2.23 settled that owner-only permissions on a
/// disk-encrypted machine are the control protecting CLI credential material — <c>tokens.json</c>,
/// <c>apikey.json</c>, <c>keys/</c>. A CI runner satisfies neither assumption: its filesystem usually
/// outlives the job, is often shared, and is rarely encrypted at rest. So the private half belongs in
/// the pipeline's own secret store and should reach the process as an environment variable or a mounted
/// file.</para>
///
/// <para><b>The obvious implementation is the wrong one</b> — dropping the private key beside the
/// session file is what a reasonable person would do, which is why FR-010 forbids it in the spec rather
/// than leaving it to judgement, and why <c>DeviceKeypairKeepsThePrivateHalfOutOfConfigTests</c> asserts
/// it rather than trusting the help text to survive a refactor.</para>
///
/// <para><b>Why produce one at all</b> rather than only accepting a key: the alternative is every
/// operator hand-rolling SPKI with <c>openssl</c>, where a wrong curve or a PEM-instead-of-DER encoding
/// produces a key the gate simply refuses — forever, with no diagnostic, because a refusal cannot
/// distinguish a mistake from an attack.</para>
/// </remarks>
internal static class DeviceKeypair
{
    /// <summary>A freshly generated P-256 keypair, both halves base64-encoded.</summary>
    /// <returns>
    /// <c>PublicKey</c> is base64 SPKI — exactly what the issuing request carries and what the gate
    /// compares byte for byte. <c>PrivateKey</c> is base64 PKCS#8 and is the caller's to place.
    /// </returns>
    internal static (string PublicKey, string PrivateKey) Create()
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return (
            Convert.ToBase64String(ecdh.ExportSubjectPublicKeyInfo()),
            Convert.ToBase64String(ecdh.ExportPkcs8PrivateKey())
        );
    }

    /// <summary>
    /// Reads a public key the operator already has, from a file, and validates it the way the service
    /// will. Failing here is far kinder than minting a token the gate can never match.
    /// </summary>
    internal static string ReadPublicKeyFile(string path)
    {
        var raw = File.ReadAllText(path).Trim();

        // Accept a PEM-wrapped key as well as bare base64: `openssl ec -pubout` emits PEM by default,
        // so refusing it would fail exactly the operator who did the sensible thing.
        if (raw.Contains("-----BEGIN", StringComparison.Ordinal))
        {
            raw = string.Concat(
                raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(line => !line.StartsWith("-----", StringComparison.Ordinal)));
        }

        Validate(raw);
        return raw;
    }

    /// <summary>
    /// The same three checks the service performs: base64, importable as SPKI, P-256. Kept in step with
    /// `IssueEnvironmentToken` deliberately — a CLI that accepted more than the service does would push
    /// the failure to a place with less context.
    /// </summary>
    internal static void Validate(string base64Spki)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64Spki);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException(
                "The public key must be base64-encoded SPKI. A PEM file is accepted too; a private key is not.");
        }

        using var ecdh = ECDiffieHellman.Create();
        try
        {
            ecdh.ImportSubjectPublicKeyInfo(bytes, out _);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException(
                "That is not a public key in SPKI form. If you exported a PRIVATE key by mistake, "
                    + "export the public half instead — the private half must never leave the machine "
                    + "that will use it.");
        }

        if (ecdh.KeySize != 256)
            throw new InvalidOperationException(
                $"The public key must be P-256; this one is {ecdh.KeySize}-bit. Enforcement compares it "
                    + "against the exact key recorded with the token, so another curve can never match.");
    }

    /// <summary>
    /// Where the private half must NOT go: anywhere inside the CLI's own configuration directory,
    /// which is where the session token and API key live.
    /// </summary>
    internal static string ConfigDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "bella-cli");

    /// <summary>
    /// True when a path an operator asked us to write to sits inside the CLI's config directory. The
    /// command refuses rather than writing there — see the type's remarks.
    /// </summary>
    internal static bool IsInsideConfigDirectory(string path)
    {
        var target = Path.GetFullPath(path);
        var config = Path.GetFullPath(ConfigDirectory);

        return target.Equals(config, StringComparison.Ordinal)
            || target.StartsWith(config + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
