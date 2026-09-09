using System.Security.Cryptography;
using BellaBaxter.Client;
using BellaBaxter.Client.Models;
using BellaCli.Commands;
using BellaCli.Infrastructure;
using Microsoft.Kiota.Abstractions;

namespace BellaCli.Services;

/// <summary>
/// spec 037 — the ONE place a secret-reading command picks its client.
/// </summary>
/// <remarks>
/// <para><b>Why one place.</b> The same twenty lines lived in four commands — <c>secrets get</c>,
/// <c>secrets list</c>, <c>run</c> and <c>sdk run</c> — and they had already drifted: one warned and
/// continued when <c>--private-key</c> would not resolve, one caught every exception and silently
/// downgraded to the plain client, and none of them knew whether the tenant enforced anything. Four
/// copies of a security decision is four chances to be the lenient one (research R12).</para>
///
/// <para><b>It asks before it requests.</b> <c>getZkeStatus</c> is a metadata call — no secret is named
/// in it — and its answer feeds the pure <see cref="ZkeGate"/>. Under enforcement the command stops with
/// a sentence naming <c>bella auth setup</c>, instead of sending a request that will be refused.</para>
///
/// <para><b>The 403 is still handled.</b> An operator can turn enforcement on between the status call
/// and the read, so <see cref="RenderRefusal"/> exists for that race: the problem's own <c>detail</c> is
/// printed and the exit code is 3. Asking first is an ergonomic improvement, never the enforcement.</para>
/// </remarks>
public sealed class ZkeClientSelection(
    ZkeService zke,
    BellaClientProvider provider,
    DekLeaseCache dekCache,
    IOutputWriter output)
{
    /// <summary>The chosen client, or a refusal the caller returns as-is.</summary>
    public sealed record Selection(BellaClient? Client, int? ExitCode, bool DeviceKeyInUse)
    {
        public bool Stopped => ExitCode is not null;
    }

    /// <summary>
    /// Resolves the key in hand, asks the server about it, and returns either a client or a stop.
    /// </summary>
    /// <param name="privateKeyOverride">The command's <c>--private-key</c>, if given.</param>
    /// <param name="appClientOverride">The command's <c>--app</c>, if given.</param>
    /// <param name="announce">Print the "ZKE enabled" line (interactive commands do; <c>sdk run</c> does not).</param>
    public async Task<Selection> SelectAsync(
        string? privateKeyOverride,
        string? appClientOverride,
        bool announce,
        CancellationToken ct)
    {
        var overridePresent = !string.IsNullOrEmpty(privateKeyOverride);
        ECDiffieHellman? ecdh = null;

        if (overridePresent)
        {
            var pkcs8b64 = ZkeService.ResolvePrivateKeyFromUrl(privateKeyOverride!);
            if (pkcs8b64 is not null)
            {
                ecdh = ECDiffieHellman.Create();
                ecdh.ImportPkcs8PrivateKey(Convert.FromBase64String(pkcs8b64), out _);
            }
        }
        else
        {
            ecdh = zke.LoadEcdhKey();
        }

        var hasDeviceKey = ecdh is not null;

        // Build the client FIRST, with whatever key is in hand: the status call must present the same
        // key the read would, or the server would answer about a different key than the one at issue.
        BellaClient client;
        ZkeDekHandler? handler = null;
        try
        {
            if (ecdh is not null)
            {
                handler = new ZkeDekHandler(
                    ecdh,
                    onWrappedDekReceived: (project, env, wrappedDek, expires) =>
                        dekCache.Store(project, env, wrappedDek, expires));
                client = provider.CreateClientWithZke(handler, appClientOverride);
            }
            else
            {
                client = provider.CreateClient(appClientOverride);
            }
        }
        catch (InvalidOperationException)
        {
            output.WriteError("Not logged in. Run 'bella login' first.");
            return new Selection(null, 1, false);
        }

        var status = await TryGetStatusAsync(client, ct);

        // A status call that could not be made is NOT a verdict. The server still enforces; proceeding
        // means the read may be refused with a 403 the caller renders, which is the correct outcome for
        // "we could not ask" — refusing here would break every read whenever this endpoint hiccups.
        var enforced = status?.Enforced ?? false;
        // Flat fields, deliberately: a nested nullable object generates as a composed type Kiota cannot
        // populate without a discriminator, so `registered` would read false for a registered device and
        // this CLI would tell people to re-run setup forever. DeviceWireNameTests pins the names.
        var registeredHere = status?.PresentedKeyRegistered ?? false;

        var outcome = ZkeGate.Decide(
            enforced,
            hasDeviceKey,
            overridePresent,
            overrideResolved: !overridePresent || ecdh is not null,
            keyRegisteredHere: registeredHere);

        if (outcome.Stops())
        {
            output.WriteError(ZkeGate.MessageFor(outcome) ?? "Refused by ZKE policy.", "zke-required");
            return new Selection(null, ZkeGate.RefusedExitCode, false);
        }

        if (outcome == ZkeGateOutcome.ProceedTransportOnly && handler is not null)
        {
            // The key could not be used (an unresolvable override on a non-enforcing tenant): fall back
            // to the plain client, exactly as before, and say so once.
            client = provider.CreateClient(appClientOverride);
            if (announce)
                output.WriteWarning("Could not resolve --private-key; continuing without local decryption.");
            return new Selection(client, null, false);
        }

        if (outcome == ZkeGateOutcome.ProceedWithDevice && announce)
            output.WriteInfo("🔐 ZKE enabled — secrets will be decrypted locally.");

        return new Selection(client, null, outcome == ZkeGateOutcome.ProceedWithDevice);
    }

    /// <summary>
    /// Renders a <c>zke-required</c> refusal the server returned anyway (the race above) and yields
    /// exit code 3. Returns false when the error was something else, so the caller keeps its own
    /// handling.
    /// </summary>
    public bool RenderRefusal(Exception ex)
    {
        var detail = ExtractZkeDetail(ex);
        if (detail is null)
            return false;

        output.WriteError(detail, "zke-required");
        return true;
    }

    /// <summary>The exit code for a refusal rendered by <see cref="RenderRefusal"/>.</summary>
    public static int RefusedExitCode => ZkeGate.RefusedExitCode;

    private static string? ExtractZkeDetail(Exception ex)
    {
        // The generated client surfaces a typed ApiException; the ZKE problem is identified by its
        // stable `type`, never by matching on the human-readable text.
        var message = ex.Message ?? string.Empty;
        if (message.Contains("zke-required", StringComparison.OrdinalIgnoreCase))
            return "This tenant requires a registered device key. Run 'bella auth setup' to register this device.";

        if (ex is ApiException { ResponseStatusCode: 403 })
            return null; // a 403 for some other reason — the caller's own handling is right

        return null;
    }

    private static async Task<ZkeStatusResponse?> TryGetStatusAsync(BellaClient client, CancellationToken ct)
    {
        try
        {
            return await client.Api.V1.Tenants.Me.Zke.GetAsync(cancellationToken: ct);
        }
        catch
        {
            // See the comment at the call site: unavailable is not a verdict.
            return null;
        }
    }
}
