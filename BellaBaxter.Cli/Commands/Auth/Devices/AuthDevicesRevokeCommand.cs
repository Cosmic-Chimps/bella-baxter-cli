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

    [System.ComponentModel.Description("Do not ask for confirmation. Never implies --rotate")]
    [CommandOption("-y|--yes")]
    public bool Yes { get; set; }

    [System.ComponentModel.Description(
        "After revoking, rotate the data keys the device received and re-seal what they protected (runs in the background)")]
    [CommandOption("--rotate")]
    public bool Rotate { get; set; }

    [System.ComponentModel.Description("Output format: table (default) or json. json prints one document and never asks")]
    [CommandOption("-o|--output <format>")]
    public string? Output { get; set; }
}

/// <summary>
/// bella auth devices revoke — withdraw a machine's ability to read secrets, and optionally rotate the data keys it was
/// handed. spec 037 US3, spec 099.
/// </summary>
/// <remarks>
/// Effective on that machine's very next request: the server holds no device cache, by design. The flow is
/// <see cref="DeviceRevocation"/> (preview, revoke, rotate); this class wires the terminal and the exit code.
/// Exit codes: 0, 1, and 2 (revoked, but <c>--rotate</c> did not start the rotation).
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
        CancellationToken ct)
    {
        var client = provider.CreateClient();

        var options = new DeviceRevocation.Options(
            settings.Device,
            settings.Rotate,
            settings.Yes,
            Json: string.Equals(settings.Output, "json", StringComparison.OrdinalIgnoreCase),
            Interactive: Interactivity.IsInteractive(output),
            ThisMachineFingerprint: BellaBaxter.Crypto.DeviceFingerprint.ComputeFromBase64(zke.GetPublicKey()),
            CallerIsApiKey: provider.UsesApiKey());

        return await DeviceRevocation.RunAsync(
            client, options, output, question => AnsiConsole.Confirm(question, defaultValue: false), ct);
    }
}
