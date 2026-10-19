using System.Security.Cryptography;
using BellaBaxter.Client;
using BellaBaxter.Client.Models;
using BellaCli.Commands;
using BellaCli.Commands.Issue;
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
    CredentialStore credentials,
    DekLeaseCache dekCache,
    IOutputWriter output
)
{
    /// <summary>The variable a keyless sign-in exports its device key in, and the one the SDKs read.</summary>
    public const string PrivateKeyVariable = "BELLA_BAXTER_PRIVATE_KEY";

    /// <summary>The variable as the CLI's own <c>--private-key</c> reader takes it (PEM or base64 PKCS#8).</summary>
    private const string PrivateKeyUrl = "env://BELLA_BAXTER_PRIVATE_KEY";

    /// <summary>The chosen client, or a refusal the caller returns as-is.</summary>
    /// <param name="Key">
    /// The key the client presents, when one is in use. Kept so a caller that must hand the SAME key to a child process
    /// (<c>sdk run</c>) asks the selection rather than re-reading the device file and possibly disagreeing with the
    /// key the gate just asked the server about.
    /// </param>
    public sealed record Selection(BellaClient? Client, int? ExitCode, bool DeviceKeyInUse, ECDiffieHellman? Key = null)
    {
        public bool Stopped => ExitCode is not null;

        /// <summary>The private key in use as PKCS#8 PEM, or null when no key is (stopped, or transport-only).</summary>
        public string? ExportPrivateKeyPem() => Key is null ? null : DeviceKeypair.ExportPrivateKeyPem(Key);
    }

    /// <summary>
    /// Resolves the key in hand, asks the server about it, and returns either a client or a stop.
    /// </summary>
    /// <remarks>
    /// The key is, in order: the command's explicit <c>--private-key</c>; else <c>BELLA_BAXTER_PRIVATE_KEY</c> (spec 085 —
    /// a CI step that holds the key a keyless sign-in exported IS that device, and has no device file); else the device
    /// file on this machine. A variable that is set but cannot be read is NOT skipped in favour of the device file: it
    /// is a stated intention that failed, so it stops under enforcement exactly as an unreadable <c>--private-key</c> does.
    /// </remarks>
    /// <param name="privateKeyOverride">The command's <c>--private-key</c>, if given.</param>
    /// <param name="appClientOverride">The command's <c>--app</c>, if given.</param>
    /// <param name="announce">Print the "ZKE enabled" line (interactive commands do; <c>sdk run</c> does not).</param>
    public async Task<Selection> SelectAsync(
        string? privateKeyOverride,
        string? appClientOverride,
        bool announce,
        CancellationToken ct
    )
    {
        var overridePresent = !string.IsNullOrEmpty(privateKeyOverride);
        var environmentKeyPresent = !overridePresent
            && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PrivateKeyVariable));
        ECDiffieHellman? ecdh;

        if (overridePresent)
            ecdh = TryImport(privateKeyOverride!);
        else if (environmentKeyPresent)
            ecdh = TryImport(PrivateKeyUrl);
        else
            ecdh = zke.LoadEcdhKey();

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
                        dekCache.Store(project, env, wrappedDek, expires)
                );
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
            keyRegisteredHere: registeredHere,
            environmentKeyUnresolvable: environmentKeyPresent && ecdh is null
        );

        if (outcome.Stops())
        {
            // The tenant the refusal is about, named (#820). The gate's slug-aware branch existed from
            // the start and no production call site ever passed one, so every rejection printed the
            // placeholder as if it were the slug — useless to the operator it is written for, who works
            // in several orgs and has to choose between 'bella auth setup' and 'bella org switch'.
            var tenantSlug = credentials.LoadTokens()?.OrgSlug;

            output.WriteError(
                ZkeGate.MessageFor(outcome, tenantSlug, IsKeylessKey()) ?? "Refused by ZKE policy.",
                "zke-required"
            );
            ecdh?.Dispose();
            return new Selection(null, ZkeGate.RefusedExitCode, false);
        }

        if (outcome == ZkeGateOutcome.ProceedTransportOnly && handler is not null)
        {
            // The key could not be used (an unresolvable override on a non-enforcing tenant): fall back
            // to the plain client, exactly as before, and say so once.
            client = provider.CreateClient(appClientOverride);
            if (announce)
                output.WriteWarning(
                    "Could not resolve --private-key; continuing without local decryption."
                );
            return new Selection(client, null, false);
        }

        if (outcome == ZkeGateOutcome.ProceedTransportOnly && environmentKeyPresent && ecdh is null && announce)
            output.WriteWarning(
                $"Could not read {PrivateKeyVariable}; continuing without local decryption."
            );

        if (outcome == ZkeGateOutcome.ProceedWithDevice && announce)
            output.WriteInfo("🔐 ZKE enabled — secrets will be decrypted locally.");

        var inUse = outcome == ZkeGateOutcome.ProceedWithDevice;
        return new Selection(client, null, inUse, inUse ? ecdh : null);
    }

    /// <summary>
    /// Reads a private key from a <c>--private-key</c> style URL, or null when it is not there or is not a P-256 key.
    /// An unreadable key is the SAME outcome as a missing one (null), never an exception: a value the user pasted wrong
    /// is a refusal the gate can word, not a stack trace.
    /// </summary>
    private static ECDiffieHellman? TryImport(string url)
    {
        byte[]? pkcs8 = null;
        ECDiffieHellman? ecdh = null;
        try
        {
            var pkcs8b64 = ZkeService.ResolvePrivateKeyFromUrl(url);
            if (pkcs8b64 is null)
                return null;

            // Zeroed after the import (#829). The base64 STRING it was decoded from cannot be —
            // see ZkeService.ImportStoredKey for why that is a storage-format problem and not an
            // oversight here.
            pkcs8 = Convert.FromBase64String(pkcs8b64);
            ecdh = ECDiffieHellman.Create();
            ecdh.ImportPkcs8PrivateKey(pkcs8, out _);
            return ecdh;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            ecdh?.Dispose();
            return null;
        }
        finally
        {
            if (pkcs8 is not null)
                CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    /// <summary>
    /// The API key in use is one a keyless sign-in minted: a <c>bax-</c> key handed in by the environment (which is how
    /// <c>bella auth oidc</c> and the setup action export it). No person is signed in behind it, so no advice that
    /// starts with an interactive login can apply.
    /// </summary>
    private bool IsKeylessKey() =>
        credentials.LoadApiKey() is { FromEnvironment: true } key
        && key.Raw.StartsWith("bax-", StringComparison.Ordinal);

    /// <summary>
    /// Renders a <c>zke-required</c> refusal the server returned anyway (the race above) and yields
    /// exit code 3. Returns false when the error was something else, so the caller keeps its own
    /// handling.
    /// </summary>
    public bool RenderRefusal(Exception ex)
    {
        var detail = ExtractZkeDetail(ex, IsKeylessKey());
        if (detail is null)
            return false;

        output.WriteError(detail, "zke-required");
        return true;
    }

    /// <summary>The exit code for a refusal rendered by <see cref="RenderRefusal"/>.</summary>
    public static int RefusedExitCode => ZkeGate.RefusedExitCode;

    private static string? ExtractZkeDetail(Exception ex, bool keylessKey)
    {
        // The generated client surfaces a typed ApiException; the ZKE problem is identified by its
        // stable `type`, never by matching on the human-readable text.
        var message = ex.Message ?? string.Empty;
        if (message.Contains("zke-required", StringComparison.OrdinalIgnoreCase))
            return keylessKey
                ? "This tenant requires a registered device key, and this key has none bound to it. " + ZkeGate.KeylessFix
                : "This tenant requires a registered device key. Run 'bella auth setup' to register this device.";

        if (ex is ApiException { ResponseStatusCode: 403 })
            return null; // a 403 for some other reason — the caller's own handling is right

        return null;
    }

    private static async Task<ZkeStatusResponse?> TryGetStatusAsync(
        BellaClient client,
        CancellationToken ct
    )
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
