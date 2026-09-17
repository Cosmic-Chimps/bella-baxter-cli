namespace BellaCli.Commands;

/// <summary>What a read command should do about ZKE before it asks for a secret.</summary>
public enum ZkeGateOutcome
{
    /// <summary>No device key in play; use the plain (transport-only) client, as before.</summary>
    ProceedTransportOnly,

    /// <summary>Use the device-key client — the caller holds a key the server will accept.</summary>
    ProceedWithDevice,

    /// <summary>Enforced, and this machine has no device key at all.</summary>
    StopNeedsSetup,

    /// <summary>Enforced, and the key this machine holds is not registered in this tenant.</summary>
    StopNotRegisteredHere,

    /// <summary>Enforced, and an explicit <c>--private-key</c> could not be resolved.</summary>
    StopUnresolvableKey,
}

/// <summary>
/// spec 037 — the CLI's pure decision: given what the server said about this tenant and this key,
/// should the command proceed, and with which client?
/// </summary>
/// <remarks>
/// <para><b>Why the CLI asks first.</b> Under enforcement, requesting a secret without a registered key
/// produces a 403 the user cannot act on from the error alone. The CLI now asks a metadata question
/// (<c>getZkeStatus</c>, readable by any member — which is the whole reason that endpoint exists) and
/// stops with the sentence that names the fix. FR-019 is worded as "MUST NOT issue the secret request",
/// not "before contacting the server", because the flag is server state and cannot be known otherwise.</para>
///
/// <para><b>Pure, and separate from the plumbing</b> — the <c>LoginGate</c>/<c>ServerProbe</c> shape from
/// the pilot PRs. Every row of the table in contracts/cli.md is a unit test with no HTTP, no filesystem
/// and no console.</para>
///
/// <para><b>The CLI never creates or registers a key inside a read command</b> (FR-019). Creating an
/// identity is a person's decision, taken once, deliberately, by running <c>bella auth setup</c>.
/// Silently minting one here would register a device nobody chose to register, in whatever tenant
/// happened to be active.</para>
/// </remarks>
public static class ZkeGate
{
    /// <summary>
    /// Exit code for "refused by policy; operator action required" — distinct from 1 (error) and
    /// 2 (usage), matching the <c>certs import</c> convention (0/1/2/3). A script can tell "your
    /// device is not registered" from "the network is down" without parsing text.
    /// </summary>
    public const int RefusedExitCode = 3;

    /// <param name="enforced">The tenant's ZKE enforcement flag, from <c>getZkeStatus</c>.</param>
    /// <param name="hasDeviceKey">A usable key is in hand (stored device key, or a resolved override).</param>
    /// <param name="overridePresent"><c>--private-key</c> was given on the command line.</param>
    /// <param name="overrideResolved">…and it resolved to a usable key.</param>
    /// <param name="keyRegisteredHere">The server says the presented key is registered in THIS tenant.</param>
    public static ZkeGateOutcome Decide(
        bool enforced,
        bool hasDeviceKey,
        bool overridePresent,
        bool overrideResolved,
        bool keyRegisteredHere)
    {
        // An explicit --private-key that did not resolve is a stated intention that failed. Under
        // enforcement this stops; it used to print a warning and read in the clear, which is the
        // opposite of what the flag asked for (FR-021).
        if (overridePresent && !overrideResolved)
            return enforced ? ZkeGateOutcome.StopUnresolvableKey : ZkeGateOutcome.ProceedTransportOnly;

        if (!enforced)
            return hasDeviceKey ? ZkeGateOutcome.ProceedWithDevice : ZkeGateOutcome.ProceedTransportOnly;

        if (!hasDeviceKey)
            return ZkeGateOutcome.StopNeedsSetup;

        return keyRegisteredHere ? ZkeGateOutcome.ProceedWithDevice : ZkeGateOutcome.StopNotRegisteredHere;
    }

    /// <summary>
    /// The sentence shown when the gate stops, or null when it does not. Every one names the command
    /// that fixes it — a refusal an operator cannot act on is only half a refusal.
    /// </summary>
    public static string? MessageFor(ZkeGateOutcome outcome, string? tenantSlug = null) => outcome switch
    {
        ZkeGateOutcome.StopNeedsSetup =>
            "This tenant requires a registered device key. Run 'bella auth setup'.",
        ZkeGateOutcome.StopNotRegisteredHere =>
            $"Your device key is not registered in {NameTenant(tenantSlug)}. Run 'bella auth setup' "
            + "(or 'bella org switch' if you meant another org).",
        ZkeGateOutcome.StopUnresolvableKey =>
            "This tenant requires a registered device key, and --private-key could not be resolved.",
        _ => null,
    };

    /// <summary>
    /// The tenant as the sentence should name it (#820).
    /// </summary>
    /// <remarks>
    /// The fallback sits OUTSIDE the quotes deliberately. Written as
    /// <c>tenant '{tenantSlug ?? "this tenant"}'</c>, an unknown slug printed as <c>tenant 'this
    /// tenant'</c> — quoted exactly like a real slug, so an operator working across several orgs read it
    /// as the name of the tenant refusing them and had no way to tell it was a placeholder. A refusal
    /// that names the wrong thing is worse than one that names nothing.
    /// </remarks>
    private static string NameTenant(string? tenantSlug) =>
        string.IsNullOrWhiteSpace(tenantSlug) ? "this tenant" : $"tenant '{tenantSlug}'";

    /// <summary>True when the command must stop rather than issue the secret request.</summary>
    public static bool Stops(this ZkeGateOutcome outcome) =>
        outcome is ZkeGateOutcome.StopNeedsSetup
            or ZkeGateOutcome.StopNotRegisteredHere
            or ZkeGateOutcome.StopUnresolvableKey;
}
