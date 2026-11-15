using BellaBaxter.Client.Models;

namespace BellaCli.Services;

/// <summary>
/// Spec 108 (FR-017) — what <c>GET /api/v1/users/me</c> says about the caller's organisation: <c>hasOrganisation</c>, and
/// while it is false the API's own sentence (<c>organisationSetupDetail</c>) and link (<c>organisationSetupUrl</c>). The CLI
/// prints those, never its own wording, so the sentence and the link are the same on every surface.
/// </summary>
/// <remarks>
/// Read from the generated model's typed properties. The public CLI repo builds against the <c>BellaBaxter.Client</c> package,
/// so it needs a Client release that carries these members before it compiles (the rule for any SDK change, docs/agents/cli.md).
/// </remarks>
internal static class OrganisationSetupAnswer
{
    /// <summary>
    /// Null when the answer does not say (an older server that does not send <c>hasOrganisation</c>): nothing is printed then.
    /// </summary>
    public static OrganisationSetupResult? Read(CurrentUserResponse? me)
    {
        if (me?.HasOrganisation is not { } hasOrganisation)
            return null;

        if (hasOrganisation)
            return new OrganisationSetupResult(false, string.Empty, null);

        var detail = Clean(me.OrganisationSetupDetail);
        var url = Clean(me.OrganisationSetupUrl);
        return new OrganisationSetupResult(
            true,
            string.IsNullOrWhiteSpace(detail) ? OrganisationSetupRequiredException.DefaultDetail : detail,
            string.IsNullOrWhiteSpace(url) ? null : url);
    }

    /// <summary>Printed to a terminal: control characters are dropped, as in <see cref="OrganisationSetupRefusal"/>.</summary>
    private static string? Clean(string? text) =>
        text is null ? null : new string(text.Where(c => !char.IsControl(c)).ToArray());
}

/// <summary>The decoded answer: <see cref="Required"/> is true when the person has no organisation yet.</summary>
internal sealed record OrganisationSetupResult(bool Required, string Detail, string? SetupUrl);
