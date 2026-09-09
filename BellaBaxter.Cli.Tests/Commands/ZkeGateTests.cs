using BellaCli.Commands;

namespace BellaBaxter.Cli.Tests.Commands;

/// <summary>
/// spec 037 (T018) — the CLI's pure decision, one test per row of contracts/cli.md.
/// </summary>
/// <remarks>
/// The decision is separated from the plumbing precisely so this file needs no HTTP, no filesystem and
/// no console — the <c>LoginGate</c>/<c>ServerProbe</c> shape. Every stop below is a case where the CLI
/// used to send a request that could only be refused, and print a 403 the user could not act on.
/// </remarks>
public class ZkeGateTests
{
    [Fact]
    public void Enforcement_off_with_no_key_proceeds_on_the_plain_client()
    {
        var outcome = ZkeGate.Decide(
            enforced: false, hasDeviceKey: false, overridePresent: false, overrideResolved: true, keyRegisteredHere: false);

        Assert.Equal(ZkeGateOutcome.ProceedTransportOnly, outcome);
        Assert.False(outcome.Stops());
    }

    [Fact]
    public void Enforcement_off_with_a_key_uses_the_device_client()
    {
        var outcome = ZkeGate.Decide(
            enforced: false, hasDeviceKey: true, overridePresent: false, overrideResolved: true, keyRegisteredHere: false);

        Assert.Equal(ZkeGateOutcome.ProceedWithDevice, outcome);
    }

    [Fact]
    public void Enforcement_on_with_a_registered_key_proceeds()
    {
        var outcome = ZkeGate.Decide(
            enforced: true, hasDeviceKey: true, overridePresent: false, overrideResolved: true, keyRegisteredHere: true);

        Assert.Equal(ZkeGateOutcome.ProceedWithDevice, outcome);
        Assert.False(outcome.Stops());
    }

    [Fact]
    public void Enforcement_on_with_no_key_stops_and_names_auth_setup()
    {
        var outcome = ZkeGate.Decide(
            enforced: true, hasDeviceKey: false, overridePresent: false, overrideResolved: true, keyRegisteredHere: false);

        Assert.Equal(ZkeGateOutcome.StopNeedsSetup, outcome);
        Assert.True(outcome.Stops());
        Assert.Contains("bella auth setup", ZkeGate.MessageFor(outcome));
    }

    [Fact]
    public void Enforcement_on_with_a_key_registered_somewhere_else_stops_and_names_the_tenant()
    {
        // FR-002 as an operator experiences it: the same laptop, registered at another customer, is not
        // a device here — and the message has to say which tenant, or it reads as a bug.
        var outcome = ZkeGate.Decide(
            enforced: true, hasDeviceKey: true, overridePresent: false, overrideResolved: true, keyRegisteredHere: false);

        Assert.Equal(ZkeGateOutcome.StopNotRegisteredHere, outcome);
        Assert.Contains("acme", ZkeGate.MessageFor(outcome, "acme"));
        Assert.Contains("bella auth setup", ZkeGate.MessageFor(outcome, "acme"));
    }

    [Fact]
    public void An_unresolvable_private_key_stops_under_enforcement()
    {
        // FR-021: this used to print "⚠ Could not resolve --private-key; ZKE disabled" and read the
        // secrets in the clear — the exact opposite of what the operator asked for by passing the flag.
        var outcome = ZkeGate.Decide(
            enforced: true, hasDeviceKey: false, overridePresent: true, overrideResolved: false, keyRegisteredHere: false);

        Assert.Equal(ZkeGateOutcome.StopUnresolvableKey, outcome);
        Assert.True(outcome.Stops());
    }

    [Fact]
    public void An_unresolvable_private_key_does_NOT_stop_when_the_tenant_does_not_enforce()
    {
        // Unchanged behaviour where no policy is in play: warn and continue, as before (SC-003).
        var outcome = ZkeGate.Decide(
            enforced: false, hasDeviceKey: false, overridePresent: true, overrideResolved: false, keyRegisteredHere: false);

        Assert.Equal(ZkeGateOutcome.ProceedTransportOnly, outcome);
        Assert.False(outcome.Stops());
    }

    [Fact]
    public void The_refusal_exit_code_is_3_and_distinct_from_error_and_usage()
    {
        // A script must be able to tell "your device is not registered" from "the network is down"
        // without parsing text. Matches the certs import convention (0/1/2/3).
        Assert.Equal(3, ZkeGate.RefusedExitCode);
    }

    [Fact]
    public void Only_the_stopping_outcomes_carry_a_message()
    {
        Assert.Null(ZkeGate.MessageFor(ZkeGateOutcome.ProceedWithDevice));
        Assert.Null(ZkeGate.MessageFor(ZkeGateOutcome.ProceedTransportOnly));
        Assert.NotNull(ZkeGate.MessageFor(ZkeGateOutcome.StopNeedsSetup));
        Assert.NotNull(ZkeGate.MessageFor(ZkeGateOutcome.StopNotRegisteredHere));
        Assert.NotNull(ZkeGate.MessageFor(ZkeGateOutcome.StopUnresolvableKey));
    }
}
