using System.Security.Cryptography;
using BellaCli.Commands.Issue;

namespace BellaBaxter.Cli.Tests.Commands.Issue;

/// <summary>
/// spec 040 US4 (T017/T018) — the private half of a runner's keypair must never be written beside the
/// CLI's own credentials.
/// </summary>
/// <remarks>
/// <para><b>This test exists because the obvious implementation is the wrong one.</b> The CLI already
/// owns <c>~/.config/bella-cli</c> — <c>tokens.json</c>, <c>apikey.json</c>, <c>keys/</c> — so dropping
/// a generated private key there is the natural thing to reach for. Backlog §2.23 settled that the
/// control protecting that directory is owner-only permissions on a disk-encrypted personal machine. A
/// CI runner satisfies neither assumption: its filesystem commonly outlives the job, is often shared
/// between jobs, and is rarely encrypted at rest.</para>
///
/// <para><b>Help text would not survive a refactor; this will.</b> FR-010 states the rule, the command
/// prints it, and this asserts it.</para>
/// </remarks>
public class DeviceKeypairKeepsThePrivateHalfOutOfConfigTests
{
    [Fact]
    public void A_generated_pair_is_a_real_P256_keypair()
    {
        var (publicKey, privateKey) = DeviceKeypair.Create();

        // The public half must satisfy the SAME validation the service applies, or the CLI would hand
        // out tokens the gate can never match.
        DeviceKeypair.Validate(publicKey);

        using var ecdh = ECDiffieHellman.Create();
        ecdh.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKey), out _);
        Assert.Equal(256, ecdh.KeySize);
    }

    [Fact]
    public void The_two_halves_belong_to_each_other()
    {
        var (publicKey, privateKey) = DeviceKeypair.Create();

        using var fromPrivate = ECDiffieHellman.Create();
        fromPrivate.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKey), out _);

        // A pair whose halves did not match would fail only at first use, inside a pipeline, as an
        // opaque refusal — exactly the failure mode FR-005 exists to avoid.
        Assert.Equal(
            publicKey,
            Convert.ToBase64String(fromPrivate.ExportSubjectPublicKeyInfo()));
    }

    [Fact]
    public void Paths_inside_the_CLI_config_directory_are_recognised()
    {
        var config = DeviceKeypair.ConfigDirectory;

        Assert.True(DeviceKeypair.IsInsideConfigDirectory(config));
        Assert.True(DeviceKeypair.IsInsideConfigDirectory(Path.Combine(config, "runner.key")));
        Assert.True(DeviceKeypair.IsInsideConfigDirectory(Path.Combine(config, "keys", "runner.key")));
    }

    [Fact]
    public void A_path_merely_starting_with_the_same_characters_is_not_inside_it()
    {
        // `~/.config/bella-cli-backup` is NOT inside `~/.config/bella-cli`. A naive StartsWith without
        // the separator would refuse a legitimate path — a guard that cries wolf gets removed.
        Assert.False(DeviceKeypair.IsInsideConfigDirectory(DeviceKeypair.ConfigDirectory + "-backup"));
    }

    [Fact]
    public void An_ordinary_working_path_is_allowed()
    {
        Assert.False(DeviceKeypair.IsInsideConfigDirectory(Path.Combine(Path.GetTempPath(), "runner.key")));
    }

    [Fact]
    public void Generating_a_pair_writes_nothing_to_the_config_directory()
    {
        var config = DeviceKeypair.ConfigDirectory;
        var before = Directory.Exists(config)
            ? Directory.GetFiles(config, "*", SearchOption.AllDirectories).ToHashSet(StringComparer.Ordinal)
            : [];

        var (_, privateKey) = DeviceKeypair.Create();
        Assert.False(string.IsNullOrWhiteSpace(privateKey));

        var after = Directory.Exists(config)
            ? Directory.GetFiles(config, "*", SearchOption.AllDirectories).ToHashSet(StringComparer.Ordinal)
            : [];

        // The whole rule in one assertion: producing a keypair is a pure operation. Where the private
        // half goes is the operator's decision, and ours is to not make it for them.
        Assert.Empty(after.Except(before));
    }

    [Fact]
    public void A_private_key_offered_where_a_public_one_belongs_is_refused()
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var privateKey = Convert.ToBase64String(ecdh.ExportPkcs8PrivateKey());

        // The mistake worth catching: an operator exports the wrong half and pastes it. Recording it
        // would send a private key to the server — the one thing this feature must never do.
        var ex = Assert.Throws<InvalidOperationException>(() => DeviceKeypair.Validate(privateKey));
        Assert.Contains("private", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_well_formed_key_of_the_wrong_curve_is_refused()
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384);
        var wrongCurve = Convert.ToBase64String(ecdh.ExportSubjectPublicKeyInfo());

        // Parses as SPKI, so a "does it decode" check would accept it, and the gate would then refuse
        // the runner forever with no clue why.
        var ex = Assert.Throws<InvalidOperationException>(() => DeviceKeypair.Validate(wrongCurve));
        Assert.Contains("P-256", ex.Message, StringComparison.Ordinal);
    }
}
