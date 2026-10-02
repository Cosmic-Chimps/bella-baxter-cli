using System.Text.Json;
using BellaBaxter.Client.Models;
using BellaCli.Commands.Spiffe;

namespace BellaBaxter.Cli.Tests.Commands;

/// <summary>
/// Spec 065 US3: what <c>bella spiffe node-bindings</c> prints, and what <c>release</c> says before it acts.
/// </summary>
/// <remarks>
/// The CLI's JSON is a contract scripts depend on, so its keys are pinned. It must never carry a credential or a
/// digest: the API does not send one, and a projection that grew a field "for completeness" is how one would leak.
/// The release consequence is pinned too, because the confirmation prompt is the operator's only chance to learn
/// that releasing re-runs trust on first use.
/// </remarks>
public class SpiffeNodeBindingsCommandTests
{
    [Fact]
    public void The_JSON_projection_has_exactly_the_contract_keys_and_no_secret_shaped_one()
    {
        var binding = new NodeBindingResponse
        {
            Id = Guid.NewGuid(),
            InstanceId = "i-0abc",
            Account = "123456789012",
            Region = "eu-west-1",
            WorkloadIdentityId = Guid.NewGuid(),
            WorkloadName = "billing",
            BoundAt = DateTimeOffset.UtcNow,
            LastAttestedAt = DateTimeOffset.UtcNow,
            LastAdmission = "credential",
            Released = false,
        };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(SpiffeNodeBindingsCommand.Json(binding)));
        var keys = json.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal);

        Assert.Equal(
            new[]
            {
                "account", "boundAt", "id", "instanceId", "lastAdmission", "lastAttestedAt", "region", "releaseReason",
                "released", "releasedAt", "workloadIdentityId", "workloadName",
            },
            keys);
        Assert.DoesNotContain(keys, k => k.Contains("credential", StringComparison.OrdinalIgnoreCase)
            || k.Contains("digest", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_missing_released_flag_reads_as_not_released()
    {
        // An older server that omits it must not render every binding as released.
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(SpiffeNodeBindingsCommand.Json(new NodeBindingResponse())));

        Assert.False(json.RootElement.GetProperty("released").GetBoolean());
    }

    [Fact]
    public void The_release_confirmation_says_that_trust_on_first_use_is_re_run()
    {
        Assert.Contains("binds afresh", SpiffeNodeBindingsReleaseCommand.Consequence, StringComparison.Ordinal);
        Assert.Contains("whichever workload attests first", SpiffeNodeBindingsReleaseCommand.Consequence, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_takes_the_binding_id_as_its_argument_and_inherits_the_context_options()
    {
        // The branch's settings are the base of the release settings, which is what lets -p/-e mean the same thing
        // on both (Spectre requires it for a branch with a default command).
        Assert.True(typeof(SpiffeNodeBindingsSettings).IsAssignableFrom(typeof(SpiffeNodeBindingsReleaseSettings)));
        var id = typeof(SpiffeNodeBindingsReleaseSettings).GetProperty(nameof(SpiffeNodeBindingsReleaseSettings.Id))!;
        Assert.Equal(typeof(Guid), id.PropertyType);
    }
}
