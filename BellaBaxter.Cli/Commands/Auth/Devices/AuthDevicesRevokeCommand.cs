using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Auth.Devices;

public class AuthDevicesRevokeSettings : CommandSettings
{
    [System.ComponentModel.Description("The device's id, or its full SHA256: fingerprint")]
    [CommandArgument(0, "<device>")]
    public string Device { get; set; } = string.Empty;

    [System.ComponentModel.Description("Do not ask for confirmation")]
    [CommandOption("-y|--yes")]
    public bool Yes { get; set; }
}

/// <summary>
/// bella auth devices revoke — withdraw a machine's ability to read secrets. spec 037 US3.
/// </summary>
/// <remarks>
/// Effective on that machine's very next request: the server holds no device cache, by design.
/// </remarks>
public class AuthDevicesRevokeCommand(
    BellaClientProvider provider,
    ZkeService zke,
    IOutputWriter output
) : AsyncCommand<AuthDevicesRevokeSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        AuthDevicesRevokeSettings settings,
        CancellationToken ct
    )
    {
        var client = provider.CreateClient();

        Guid deviceId;
        string? label = null;
        var isThisMachine = false;

        if (Guid.TryParse(settings.Device, out var parsed))
        {
            deviceId = parsed;
        }
        else
        {
            // A fingerprint is what a person has in front of them — it is what `auth status` and the
            // console both print — so accept it and resolve it here rather than making them look up an id.
            var devices = await client.Api.V1.Tenants.Me.Devices.GetAsync(
                r => r.QueryParameters.Scope = "tenant", cancellationToken: ct);

            var match = devices?.FirstOrDefault(d =>
                string.Equals(d.Fingerprint, settings.Device, StringComparison.Ordinal));

            if (match?.Id is null)
            {
                output.WriteError($"No device matches '{settings.Device}'.", "not-found");
                return 1;
            }

            deviceId = match.Id.Value;
            label = match.Label;
        }

        var thisFingerprint = BellaBaxter.Crypto.DeviceFingerprint.ComputeFromBase64(zke.GetPublicKey());
        if (thisFingerprint is not null && settings.Device == thisFingerprint)
            isThisMachine = true;

        if (!settings.Yes && Interactivity.IsInteractive(output))
        {
            AnsiConsole.MarkupLine(
                "[yellow]Revoking stops that machine from reading secrets on its next request.[/]");
            AnsiConsole.MarkupLine(
                "[yellow]It does NOT un-share what it already holds: an environment key already cached "
                + "there still decrypts the values it decrypted before.[/]");

            if (isThisMachine)
                AnsiConsole.MarkupLine("[red]This is THIS machine — your next read will be refused.[/]");

            if (!AnsiConsole.Confirm($"Revoke device {label ?? settings.Device}?", defaultValue: false))
            {
                output.WriteInfo("Nothing was revoked.");
                return 0;
            }
        }

        try
        {
            await client.Api.V1.Tenants.Me.Devices[deviceId].DeleteAsync(cancellationToken: ct);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 404)
        {
            // 404 also covers "not yours and you are not an admin" — the server does not distinguish,
            // deliberately, so neither does this message.
            output.WriteError("No such device, or it is not yours to revoke.", "not-found");
            return 1;
        }
        catch (Exception ex)
        {
            output.WriteError($"Could not revoke the device: {ex.Message}");
            return 1;
        }

        output.WriteSuccess(
            $"Device revoked.{(isThisMachine ? " This machine's next secret read will be refused; run 'bella auth setup' to register again." : "")}");
        return 0;
    }
}
