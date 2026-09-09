using BellaBaxter.Client.Models;
using BellaBaxter.Crypto;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Microsoft.Kiota.Abstractions;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Auth;

/// <summary>
/// bella auth setup — registers this machine as a ZKE device in the current tenant.
///
/// <para>Two steps, and the second one is new in spec 037:</para>
/// <list type="number">
///   <item>Generate a P-256 keypair and store the private half under <c>~/.config/bella-cli</c>,
///         owner-only (see <see cref="ZkeService"/> for why those permissions are the control).</item>
///   <item><b>REGISTER the public half with the server.</b> Before spec 037 there was no such step:
///         the public key travelled on every request and the server accepted any non-empty value, so
///         "ZKE enforcement" admitted a machine that had run nothing at all. The command's own doc
///         comment used to say "no server registration needed" — that sentence was the feature's whole
///         defect, written down.</item>
/// </list>
///
/// <para>Registration is per tenant (FR-002) and idempotent per key (FR-003), which is what makes this
/// safe to re-run: the same machine in a second tenant registers again, and re-running here just
/// updates the label.</para>
/// </summary>
public class AuthSetupSettings : CommandSettings
{
    [System.ComponentModel.Description("Generate a NEW device key, replacing the local one")]
    [Spectre.Console.Cli.CommandOption("--force")]
    public bool Force { get; set; }

    [System.ComponentModel.Description("Device label (e.g. 'MacBook Pro')")]
    [Spectre.Console.Cli.CommandOption("--device-name <name>")]
    public string? DeviceName { get; set; }
}

public class AuthSetupCommand(
    ZkeService zke,
    CredentialStore credentials,
    BellaClientProvider provider,
    IOutputWriter output
) : AsyncCommand<AuthSetupSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        AuthSetupSettings settings,
        CancellationToken ct
    )
    {
        // Checked FIRST: generating a key we then cannot register would leave an unregistered orphan
        // on disk and an operator convinced they had set something up.
        if (!credentials.IsAuthenticated())
        {
            output.WriteError("Not logged in. Run 'bella login' first.", "unauthenticated");
            return 1;
        }

        var existingKey = zke.HasKeypair() ? zke.GetPublicKey() : null;
        var generated = false;

        if (existingKey is not null && !settings.Force)
        {
            // CHANGED in spec 037. This used to warn and exit 1 whenever a key existed, which meant a
            // machine with a key but no registration — the state every machine was in before this
            // feature — had no way forward that did not involve --force and a new key. A key that
            // exists but is not registered here is exactly what this command is for.
            var already = await IsRegisteredHereAsync(ct);
            if (already is { } device)
            {
                PrintRegistered(device, "Already registered.");
                return 0;
            }
        }

        var publicKey = existingKey;
        if (publicKey is null || settings.Force)
        {
            try
            {
                publicKey = zke.GenerateAndSaveKeypair();
                generated = true;
            }
            catch (Exception ex)
            {
                output.WriteError($"Failed to generate keypair: {ex.Message}");
                return 1;
            }
        }

        try
        {
            var client = provider.CreateClient();
            var device = await client.Api.V1.Tenants.Me.Devices.PostAsync(
                new RegisterZkeDeviceRequest
                {
                    PublicKey = publicKey,
                    Label = settings.DeviceName ?? Environment.MachineName,
                },
                cancellationToken: ct);

            if (device is null)
            {
                output.WriteError("The server accepted the registration but returned nothing.");
                return 1;
            }

            PrintRegistered(device, settings.Force ? "New device registered." : "Device registered.");

            if (settings.Force)
                output.WriteWarning(
                    "Your previous device stays REGISTERED until you revoke it: 'bella auth devices list' then "
                    + "'bella auth devices revoke <id>'.");

            return 0;
        }
        catch (Exception ex)
        {
            // A generated-but-unregistered key is worse than no key: the machine looks set up, presents
            // a key on every request, and is refused with a message telling it to run this command.
            if (generated)
            {
                zke.DeleteKeypair();
                output.WriteWarning("The newly generated key was discarded — nothing was left half-configured.");
            }

            output.WriteError($"Registration was refused: {Describe(ex)}");
            return 1;
        }
    }

    /// <summary>The device this machine's key already is in this tenant, or null.</summary>
    private async Task<ZkeDeviceResponse?> IsRegisteredHereAsync(CancellationToken ct)
    {
        try
        {
            var client = provider.CreateClient();
            var devices = await client.Api.V1.Tenants.Me.Devices.GetAsync(
                r => r.QueryParameters.Scope = "mine", cancellationToken: ct);

            var fingerprint = DeviceFingerprint.ComputeFromBase64(zke.GetPublicKey());
            return devices?.FirstOrDefault(d => d.Fingerprint == fingerprint && d.State == "active");
        }
        catch
        {
            // Unknown is not "no": fall through and let the registration call be the answer. It is
            // idempotent, so asking twice costs nothing.
            return null;
        }
    }

    private void PrintRegistered(ZkeDeviceResponse device, string headline)
    {
        // The tenant the token was issued for — the one the registration landed in. Named explicitly
        // because a device is registered PER TENANT (FR-002) and a person who works in several will
        // otherwise wonder why the same laptop is refused somewhere else.
        var tenant = credentials.LoadTokens()?.OrgSlug ?? "the current tenant";

        output.WriteSuccess(
            $"✅ {headline}\n"
            + $"   Fingerprint: {device.Fingerprint}\n"
            + $"   Label:       {device.Label ?? "(none)"}\n"
            + $"   Tenant:      {tenant}\n\n"
            + "This machine can now read secrets in this tenant, and its reads are named on the audit trail.\n"
            + "Register it in another tenant by switching tenants and running this again.");
    }

    private static string Describe(Exception ex) => ex switch
    {
        ApiException { ResponseStatusCode: 409 } =>
            "that public key is already registered in this tenant by someone else.",
        ApiException { ResponseStatusCode: 400 } =>
            "the server rejected the key. Machine credentials register their public key when the API key is created.",
        _ => ex.Message,
    };
}
