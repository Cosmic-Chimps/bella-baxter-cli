using System.Security.Cryptography;
using BellaCli.Services;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// Issue #731 — the key <c>bella sdk run</c> injects must load in the SDKs that read it.
/// </summary>
/// <remarks>
/// <para><c>sdk run</c> set <c>BELLA_BAXTER_PRIVATE_KEY</c> to base64 PKCS#8 DER. Python
/// (<c>E2EKeyPair.from_pem</c>), JS (<c>fromPkcs8Pem</c>), Go (<c>PrivateKeyPEM</c>) and Java
/// (<c>privateKeyPem()</c>) all read PEM, as does <c>docs/e2ee-zke.md</c> — only the .NET SDK
/// accepted base64. So four of the five SDKs failed at client construction, and under ZKE
/// enforcement a device key is mandatory, making that the normal path rather than an edge case.</para>
///
/// <para>The July SDK matrix missed it because that CLI version did not inject the key at all. A test
/// that only asserted "a key was injected" would still pass today, which is why these assert the
/// FORMAT and then parse it.</para>
/// </remarks>
public class DeviceKeyIsInjectedAsPemTests
{
    /// <summary>A real P-256 key, as base64 PKCS#8 — what the credential file holds.</summary>
    private static string SampleBase64()
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return Convert.ToBase64String(ecdh.ExportPkcs8PrivateKey());
    }

    private static string SamplePem()
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return new string(PemEncoding.Write("PRIVATE KEY", ecdh.ExportPkcs8PrivateKey()));
    }

    [Fact]
    public void PEM_armour_is_stripped_to_the_base64_body()
    {
        var pem = SamplePem();

        var stripped = ZkeService.StripPemArmour(pem);

        Assert.DoesNotContain("-----", stripped, StringComparison.Ordinal);
        // The point of stripping: what comes out must still be a loadable key.
        using var ecdh = ECDiffieHellman.Create();
        ecdh.ImportPkcs8PrivateKey(Convert.FromBase64String(stripped), out _);
    }

    [Fact]
    public void Raw_base64_passes_through_unchanged()
    {
        // Anyone who exported base64 by hand keeps working — the change adds a format, it does not
        // swap one for another.
        var base64 = SampleBase64();

        Assert.Equal(base64, ZkeService.StripPemArmour(base64));
    }

    [Fact]
    public void Windows_line_endings_do_not_break_a_perfectly_good_key()
    {
        // A key that has been through a CI variable, an editor or a copy-paste is the same key.
        // Failing here would present as "malformed key" for a file the operator can see is correct.
        var crlf = SamplePem().Replace("\n", "\r\n");

        var stripped = ZkeService.StripPemArmour(crlf);

        using var ecdh = ECDiffieHellman.Create();
        ecdh.ImportPkcs8PrivateKey(Convert.FromBase64String(stripped), out _);
    }

    [Fact]
    public void The_env_reader_accepts_what_sdk_run_now_writes()
    {
        // The regression this fix could have caused. `ResolvePrivateKeyFromUrl` handled PEM for
        // `file://` and for a bare path, but the `env://` branch returned the variable RAW — and
        // `env://` is precisely what `sdk run` writes into and what `pull --private-key env://`
        // reads back. Emitting PEM without this would have fixed four SDKs and broken the CLI's own
        // round trip.
        var name = $"BELLA_TEST_KEY_{Guid.NewGuid():N}";
        var pem = SamplePem();
        Environment.SetEnvironmentVariable(name, pem);

        try
        {
            var resolved = ZkeService.ResolvePrivateKeyFromUrl($"env://{name}");

            Assert.NotNull(resolved);
            Assert.DoesNotContain("-----", resolved!, StringComparison.Ordinal);

            using var ecdh = ECDiffieHellman.Create();
            ecdh.ImportPkcs8PrivateKey(Convert.FromBase64String(resolved!), out _);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void The_env_reader_still_accepts_base64()
    {
        var name = $"BELLA_TEST_KEY_{Guid.NewGuid():N}";
        var base64 = SampleBase64();
        Environment.SetEnvironmentVariable(name, base64);

        try
        {
            Assert.Equal(base64, ZkeService.ResolvePrivateKeyFromUrl($"env://{name}"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void A_file_url_and_an_env_url_yield_the_SAME_key_for_the_same_PEM()
    {
        // The formats are interchangeable at the boundary, which is the property that lets `sdk run`
        // change what it emits without every consumer having to agree in the same release.
        var pem = SamplePem();
        var path = Path.Combine(Path.GetTempPath(), $"bella-key-{Guid.NewGuid():N}.pem");
        var name = $"BELLA_TEST_KEY_{Guid.NewGuid():N}";
        File.WriteAllText(path, pem);
        Environment.SetEnvironmentVariable(name, pem);

        try
        {
            Assert.Equal(
                ZkeService.ResolvePrivateKeyFromUrl($"file://{path}"),
                ZkeService.ResolvePrivateKeyFromUrl($"env://{name}"));
        }
        finally
        {
            File.Delete(path);
            Environment.SetEnvironmentVariable(name, null);
        }
    }
}
