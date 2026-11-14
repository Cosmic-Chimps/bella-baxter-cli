using System.Text.Json;
using System.Text.Json.Nodes;
using BellaBaxter.Client;
using BellaBaxter.Client.Models;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Serialization;

namespace BellaCli.Commands.Auth.Devices;

/// <summary>
/// The flow of <c>bella auth devices revoke</c> (spec 099, contracts/cli.md): resolve the device, preview what it was handed,
/// revoke, and when asked start the data-key rotation. It lives apart from the command so the flow can be driven with a client
/// over a stub handler; the command only wires the terminal, the client and the exit code.
/// </summary>
/// <remarks>
/// <para><b>The revoke always happens first and is never rolled back by a rotation failure</b> (FR-005). A failed rotation after a
/// successful revoke is exit code 2, so a script can tell "revoked, rotation still owed" from a failure.</para>
/// <para><b>Prompts are never asked in JSON mode, and <c>--yes</c> never implies <c>--rotate</c></b>: rotation happens only when it
/// was asked for, by the flag or by the person answering the second question.</para>
/// </remarks>
public static class DeviceRevocation
{
    public const int ExitOk = 0;
    public const int ExitFailure = 1;

    /// <summary>The device is revoked but <c>--rotate</c> was given and the rotation did not start.</summary>
    public const int ExitRevokedRotationNotStarted = 2;

    public sealed record Options(
        string Device,
        bool Rotate,
        bool Yes,
        bool Json,
        bool Interactive,
        string? ThisMachineFingerprint,
        bool CallerIsApiKey);

    /// <summary>The limits an operator must read before they believe rotation un-shares anything. English only: the CLI has no localization.</summary>
    public static readonly string[] LimitsCopy =
    [
        "Revoking stops access, not knowledge: the device can no longer read from Bella, but it still knows what it already read.",
        "Rotating the data keys does not make the device forget those values. Rotate the secrets themselves, at their source, for anything it read.",
        "Past versions of a secret keep the sealing they were written with, so an old version still opens with the keys the device held. "
            + "Destroying old versions is your choice.",
    ];

    /// <summary>Only said when the device read a project that was on the organisation's key.</summary>
    public const string OrganisationKeyCopy =
        "Organisation-level records (provider credentials, webhook and notification secrets, and the audit-stream credential) "
        + "stay under the organisation key and are NOT rotated by revoking. "
        + "The organisation Owner can rotate the organisation key in the console: Settings, Encryption.";

    /// <summary>Said instead of the count when the device's last read of the organisation key is older than its latest rotation (spec 106).</summary>
    public const string OrganisationKeyRotatedAfterReadCopy =
        "The organisation key was rotated after this device's last read of it.";

    /// <summary>Said when the count could not be read. A failed read is never printed as 0 (spec 106).</summary>
    public const string OrganisationKeyCountUnknownCopy =
        "The number of organisation records a rotation would re-seal could not be determined.";

    private static string OrganisationKeyOfferCopy(int records, int projects) =>
        $"Rotating the organisation key would re-seal {records} organisation {(records == 1 ? "record" : "records")} "
        + $"and give {projects} {(projects == 1 ? "project" : "projects")} their own key.";

    /// <summary>What the preview of an organisation-key rotation said. <see cref="Asked"/> is false when the device did not use the key or the key was rotated since.</summary>
    private sealed record OrganisationKeyCount(bool Asked, int? Records, int? Projects)
    {
        public static readonly OrganisationKeyCount NotAsked = new(false, null, null);
        public bool Known => Records is not null && Projects is not null;
    }

    public const string NotPermittedCopy =
        "You may not rotate those projects. Organisation Owners and Admins can rotate every affected project, and the managers of "
        + "an affected project can rotate that project. The device stays revoked.";

    private sealed record Failure(string Code, string Message);

    public static async Task<int> RunAsync(
        BellaClient client,
        Options options,
        IOutputWriter output,
        Func<string, bool> confirm,
        CancellationToken ct)
    {
        var json = options.Json || output is JsonOutputWriter;
        var interactive = options.Interactive && !json;

        void Say(string message)
        {
            if (!json)
                output.WriteInfo(message);
        }

        void Fail(string message, string? code = null)
        {
            // In JSON mode through the human writer stdout is the document's: errors are in the document instead.
            if (!json || output is JsonOutputWriter)
                output.WriteError(message, code);
        }

        // ── 1. resolve the device ─────────────────────────────────────────────────────────────────
        Guid deviceId;
        string? label = null;

        if (Guid.TryParse(options.Device, out var parsed))
        {
            deviceId = parsed;
        }
        else
        {
            // A fingerprint is what a person has in front of them (`auth status`, the console), so accept it. A member sees
            // their own devices with scope `mine`, which every caller may ask for; only when that finds nothing is the
            // tenant-wide list tried, and there a 403 just means "not an Owner/Admin": no match, not a failure.
            try
            {
                var mine = await client.Api.V1.Tenants.Me.Devices.GetAsync(
                    r => r.QueryParameters.Scope = "mine", cancellationToken: ct);
                var match = mine?.FirstOrDefault(d => string.Equals(d.Fingerprint, options.Device, StringComparison.Ordinal));

                if (match?.Id is null)
                {
                    try
                    {
                        var tenant = await client.Api.V1.Tenants.Me.Devices.GetAsync(
                            r => r.QueryParameters.Scope = "tenant", cancellationToken: ct);
                        match = tenant?.FirstOrDefault(d => string.Equals(d.Fingerprint, options.Device, StringComparison.Ordinal));
                    }
                    catch (Exception ex) when (PersonOnlyRefusal.Is(ex, options.CallerIsApiKey))
                    {
                        throw;
                    }
                    catch (ApiException ex) when (ex.ResponseStatusCode == 403)
                    {
                        match = null;
                    }
                }

                if (match?.Id is null)
                {
                    Fail($"No device matches '{options.Device}'.", "not-found");
                    return ExitFailure;
                }

                deviceId = match.Id.Value;
                label = match.Label;
            }
            catch (Exception ex) when (PersonOnlyRefusal.Is(ex, options.CallerIsApiKey))
            {
                Fail(PersonOnlyRefusal.Message, PersonOnlyRefusal.Code);
                return ExitFailure;
            }
            catch (Exception ex)
            {
                Fail($"Could not list devices: {ex.Message}");
                return ExitFailure;
            }
        }

        // ── 2. preview ────────────────────────────────────────────────────────────────────────────
        DeviceKeyExposureResponse? exposure = null;
        Failure? exposureError = null;
        try
        {
            exposure = await client.Api.V1.Tenants.Me.Devices[deviceId].KeyExposure.GetAsync(cancellationToken: ct);
        }
        catch (Exception ex) when (PersonOnlyRefusal.Is(ex, options.CallerIsApiKey))
        {
            Fail(PersonOnlyRefusal.Message, PersonOnlyRefusal.Code);
            return ExitFailure;
        }
        catch (ApiException ex) when (ex.ResponseStatusCode == 404)
        {
            Fail("No such device, or it is not yours to revoke.", "not-found");
            return ExitFailure;
        }
        catch (Exception ex)
        {
            // The preview is advice. Whatever stopped it, revoking stays possible; rotation is judged by the server later.
            exposureError = ex is ProblemDetails problem
                ? new Failure(problem.Type ?? "exposure-unavailable", problem.Detail ?? problem.Title ?? "The preview is unavailable.")
                : new Failure("exposure-unavailable", ex.Message);
        }

        label ??= exposure?.Label;
        var fingerprint = exposure?.Fingerprint ?? (options.Device.StartsWith("SHA256:", StringComparison.Ordinal) ? options.Device : null);
        var isThisMachine = options.ThisMachineFingerprint is not null
            && (string.Equals(options.Device, options.ThisMachineFingerprint, StringComparison.Ordinal)
                || string.Equals(fingerprint, options.ThisMachineFingerprint, StringComparison.Ordinal));
        var owedCount = exposure?.OwedCount ?? exposure?.Projects?.Count(p => p.Owed == true) ?? 0;

        // The count is read only when it will be shown. A failure leaves the count unknown, never zero.
        var organisationKey = OrganisationKeyCount.NotAsked;
        if (exposure?.UsedOrganisationKey == true && exposure.OrganisationKeyRotatedAfterRead != true)
            organisationKey = await ReadOrganisationKeyCountAsync(client, ct);

        PrintPreview(Say, exposure, exposureError, organisationKey);

        // ── 3. confirm ────────────────────────────────────────────────────────────────────────────
        var rotate = options.Rotate;
        if (!options.Yes && interactive)
        {
            if (isThisMachine)
                output.WriteWarning("This is THIS machine: your next read will be refused.");

            if (!confirm($"Revoke device {label ?? options.Device}?"))
            {
                output.WriteInfo("Nothing was revoked.");
                return ExitOk;
            }

            // Asked only when there is something owed and it was not already asked for. Default no.
            if (!rotate && owedCount > 0 && confirm($"Also rotate the data keys of {owedCount} project(s)?"))
                rotate = true;
        }

        // ── 4. revoke ─────────────────────────────────────────────────────────────────────────────
        try
        {
            await client.Api.V1.Tenants.Me.Devices[deviceId].DeleteAsync(cancellationToken: ct);
        }
        catch (Exception ex) when (PersonOnlyRefusal.Is(ex, options.CallerIsApiKey))
        {
            Fail(PersonOnlyRefusal.Message, PersonOnlyRefusal.Code);
            return ExitFailure;
        }
        catch (ApiException ex) when (ex.ResponseStatusCode == 404)
        {
            // 404 also covers "not yours and you are not an admin": the server does not distinguish, deliberately, so neither does this.
            Fail("No such device, or it is not yours to revoke.", "not-found");
            return ExitFailure;
        }
        catch (Exception ex)
        {
            Fail($"Could not revoke the device: {ex.Message}");
            if (json)
                output.WriteObject(await DocumentAsync(deviceId, fingerprint, label, isThisMachine, revoked: false, exposure, exposureError, organisationKey, rotate, null, null));
            return ExitFailure;
        }

        if (!json)
            output.WriteSuccess(
                $"Device revoked.{(isThisMachine ? " This machine's next secret read or credential request will be refused; run 'bella auth setup' to register again." : "")}");

        // ── 5. rotate ─────────────────────────────────────────────────────────────────────────────
        DeviceKeyRotationRunResponse? run = null;
        Failure? rotationError = null;
        var exit = ExitOk;

        if (rotate)
        {
            if (exposure is not null && owedCount == 0)
            {
                // Nothing to rotate: the same answer the server's 409 nothing-owed gives, without asking it.
                rotationError = new Failure("nothing-owed", "None of this device's projects is owed a rotation.");
                Say("Nothing is owed: there is nothing to rotate.");
            }
            else
            {
                try
                {
                    run = await client.Api.V1.Tenants.Me.Devices[deviceId].KeyRotation.PostAsync(
                        new StartDeviceKeyRotationRequest(), cancellationToken: ct);
                    if (run is null)
                        throw new InvalidOperationException("The server answered without a rotation run.");

                    Say($"Key rotation started (run {run.Id}). The new keys are in place and re-sealing runs in the background; "
                        + "progress is on the console's devices page.");
                }
                catch (Exception ex)
                {
                    rotationError = ex switch
                    {
                        _ when PersonOnlyRefusal.Is(ex, options.CallerIsApiKey) => new Failure(PersonOnlyRefusal.Code, PersonOnlyRefusal.Message),
                        ProblemDetails { Type: var type } p when type is not null => new Failure(CodeOf(type), MessageFor(CodeOf(type), p)),
                        ApiException { ResponseStatusCode: 403 } => new Failure("rotation-not-permitted", NotPermittedCopy),
                        _ => new Failure("rotation-failed", $"Could not start the key rotation: {ex.Message}"),
                    };

                    if (rotationError.Code == "nothing-owed")
                        Say(rotationError.Message);
                    else
                    {
                        Fail(rotationError.Message, rotationError.Code);
                        exit = ExitRevokedRotationNotStarted;
                        if (!json)
                            output.WriteWarning("The device is revoked, but the rotation of its data keys is still owed.");
                    }
                }
            }
        }

        if (json)
            output.WriteObject(await DocumentAsync(deviceId, fingerprint, label, isThisMachine, revoked: true, exposure, exposureError, organisationKey, rotate, run, rotationError));

        return exit;
    }

    // ── the preview ───────────────────────────────────────────────────────────────────────────────

    private static async Task<OrganisationKeyCount> ReadOrganisationKeyCountAsync(BellaClient client, CancellationToken ct)
    {
        try
        {
            var preview = await client.Api.V1.Tenants.Me.EncryptionKey.Rotation.Preview.GetAsync(cancellationToken: ct);

            // A response without the numbers is an unknown, not a zero.
            return preview?.RecordCount is { } records && preview.InheritingProjectCount is { } projects
                ? new OrganisationKeyCount(true, records, projects)
                : new OrganisationKeyCount(true, null, null);
        }
        catch (Exception)
        {
            // A 403 (not an Owner or Admin, or an API key), a 503 or a transport failure all mean the same here: advice that is unavailable.
            return new OrganisationKeyCount(true, null, null);
        }
    }

    private static void PrintPreview(Action<string> say, DeviceKeyExposureResponse? exposure, Failure? error, OrganisationKeyCount organisationKey)
    {
        if (exposure is null)
        {
            say($"The projects this device received keys for could not be determined ({error?.Code ?? "exposure-unavailable"}). "
                + "Revoking is still possible; rotating the keys is not offered until it can be.");
        }
        else if ((exposure.Projects?.Count ?? 0) == 0 && (exposure.WidenedProjectCount ?? 0) == 0)
        {
            say("This device received no keys: nothing to rotate.");
        }
        else
        {
            say($"This device received data keys for {exposure.ProjectCount ?? exposure.Projects?.Count ?? 0} project(s):");
            foreach (var p in exposure.Projects ?? [])
            {
                var environments = string.Join(", ", (p.Environments ?? []).Select(e => e.Name));
                say($"  {p.ProjectName}{(environments.Length > 0 ? $" ({environments})" : "")}");
            }

            if ((exposure.WidenedProjectCount ?? 0) > 0)
                say($"  and {exposure.WidenedProjectCount} more project(s) that share the organisation key it held.");
        }

        foreach (var limit in LimitsCopy)
            say(limit);

        if (exposure?.UsedOrganisationKey == true)
        {
            say(OrganisationKeyCopy);

            if (exposure.OrganisationKeyRotatedAfterRead == true)
                say(OrganisationKeyRotatedAfterReadCopy);
            else if (organisationKey.Known)
                say(OrganisationKeyOfferCopy(organisationKey.Records!.Value, organisationKey.Projects!.Value));
            else
                say(OrganisationKeyCountUnknownCopy);
        }
    }

    // ── errors and the JSON document ──────────────────────────────────────────────────────────────

    /// <summary>The problem <c>type</c> is a code, or a URI ending in one.</summary>
    private static string CodeOf(string type) => type.Contains('/') ? type[(type.LastIndexOf('/') + 1)..] : type;

    private static string MessageFor(string code, ProblemDetails problem) => code switch
    {
        "rotation-not-permitted" => NotPermittedCopy,
        "rotation-in-progress" => "A key rotation for this device is already running. Wait for it to finish; progress is on the console's devices page.",
        "nothing-owed" => "None of this device's projects is owed a rotation.",
        "key-store-unreadable" or "access-record-unreadable" =>
            $"The projects to rotate could not be determined ({code}), so nothing was changed. Try again once the key store and the access record are reachable.",
        _ => problem.Detail ?? problem.Title ?? code,
    };

    private static async Task<JsonObject> DocumentAsync(
        Guid deviceId,
        string? fingerprint,
        string? label,
        bool thisMachine,
        bool revoked,
        DeviceKeyExposureResponse? exposure,
        Failure? exposureError,
        OrganisationKeyCount organisationKey,
        bool rotateRequested,
        DeviceKeyRotationRunResponse? run,
        Failure? rotationError)
    {
        static JsonNode? Error(Failure? f) =>
            f is null ? null : new JsonObject { ["code"] = f.Code, ["message"] = f.Message };

        return new JsonObject
        {
            ["device"] = new JsonObject
            {
                ["id"] = deviceId.ToString(),
                ["fingerprint"] = fingerprint,
                ["label"] = label,
                ["thisMachine"] = thisMachine,
            },
            ["revoked"] = revoked,
            ["exposure"] = await WireAsync(exposure),
            ["exposureError"] = Error(exposureError),
            // Flat members, null when the question was not asked or could not be answered: a failed read is never 0.
            ["organisationKeyCountKnown"] = organisationKey.Known,
            ["organisationKeyRecordCount"] = organisationKey.Records,
            ["organisationKeyInheritingProjectCount"] = organisationKey.Projects,
            ["rotation"] = new JsonObject
            {
                ["requested"] = rotateRequested,
                ["started"] = run is not null,
                ["run"] = await WireAsync(run),
                ["error"] = rotateRequested && run is null ? Error(rotationError) : null,
            },
        };
    }

    /// <summary>A response as the server sent it (camelCase, the shape the contract names), not as a .NET object graph.</summary>
    private static async Task<JsonNode?> WireAsync(IParsable? model) =>
        model is null ? null : JsonNode.Parse(await KiotaJsonSerializer.SerializeAsStringAsync(model));
}
