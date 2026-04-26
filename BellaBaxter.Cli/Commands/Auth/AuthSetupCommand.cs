using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Auth;

/// <summary>
/// bella auth setup — generates a per-device P-256 keypair for zero-knowledge encryption.
///
/// This is a one-time command per device:
/// 1. Generates a new P-256 ECDH keypair
/// 2. Stores the private key encrypted with ASP.NET DataProtection (OS keychain-backed)
///
/// The public key is sent as X-E2E-Public-Key on each request — no server registration needed.
/// The server wraps the project DEK with the device key and returns it in X-Bella-Wrapped-Dek.
/// The CLI uses the wrapped DEK to decrypt bellabaxter:v1: prefixed values locally.
/// </summary>
public class AuthSetupSettings : CommandSettings
{
    [System.ComponentModel.Description("Force key regeneration even if one already exists")]
    [Spectre.Console.Cli.CommandOption("--force")]
    public bool Force { get; set; }

    [System.ComponentModel.Description("Device label (e.g. 'MacBook Pro')")]
    [Spectre.Console.Cli.CommandOption("--device-name <name>")]
    public string? DeviceName { get; set; }
}

public class AuthSetupCommand(
    ZkeService zke,
    CredentialStore credentials,
    IOutputWriter output
) : AsyncCommand<AuthSetupSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        AuthSetupSettings settings,
        CancellationToken ct
    )
    {
        if (zke.HasKeypair() && !settings.Force)
        {
            output.WriteWarning(
                "A device encryption key already exists. Use --force to regenerate.\n" +
                "⚠️  Regenerating will break existing encrypted secrets until you re-run bella run once."
            );
            return 1;
        }

        if (!credentials.IsAuthenticated())
        {
            output.WriteError("Not logged in. Run 'bella login' first.", "unauthenticated");
            return 1;
        }

        string publicKey;
        try
        {
            await AnsiConsole.Status().StartAsync("Generating P-256 keypair...", async _ =>
            {
                await Task.Delay(100, ct); // Give spinner a tick
                publicKey = zke.GenerateAndSaveKeypair();
            });
            publicKey = zke.GetPublicKey()!;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to generate keypair: {ex.Message}");
            return 1;
        }

        var fingerprint = ComputeFingerprint(publicKey);
        output.WriteSuccess(
            $"✅ Device encryption key set up successfully.\n" +
            $"   Fingerprint: {fingerprint}\n\n" +
            $"Your secrets will now be decrypted locally by the CLI.\n" +
            $"bella run / bella pull / bella exec will automatically decrypt bellabaxter:v1: values."
        );

        return 0;
    }

    private static string ComputeFingerprint(string base64SpkiKey)
    {
        try
        {
            var keyBytes = Convert.FromBase64String(base64SpkiKey);
            var hash = System.Security.Cryptography.SHA256.HashData(keyBytes);
            return string.Join(":", System.Convert.ToHexString(hash)
                .ToLowerInvariant()
                .Chunk(2)
                .Select(c => new string(c))
                .Take(8)) + "...";
        }
        catch
        {
            return "unknown";
        }
    }
}
