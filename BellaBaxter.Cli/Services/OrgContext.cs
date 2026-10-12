namespace BellaCli.Services;

/// <summary>
/// A directory's <c>.bella</c> names an <c>org</c>, but nothing used to act on it. For a person's OAuth
/// login the API takes its tenant from the token's <c>tenant_id</c> claim alone, and that is changed only
/// by <c>bella org switch</c>. So when <c>.bella</c> said one org and the login was in another, the
/// project lookup ran in the wrong tenant and answered "Project 'x' not found" — true, and useless.
///
/// <para>The CLI does not switch for you. <c>.bella</c> arrives with every repository you clone, and a
/// switch rewrites the person's account-wide active org (the web console included), so a file someone
/// else committed must not be able to do that. It refuses, before any request, and says what to run.</para>
///
/// <para>This is the rule; <c>ContextService</c> is the only caller, and only when the project comes from
/// the directory or session context (an explicit <c>--project</c> is the caller's own statement) and the
/// stored login is the credential being sent.</para>
/// </summary>
public static class OrgContext
{
    /// <summary>
    /// Returns the refusal message, or null when the two orgs agree or when either side is not known —
    /// a <c>.bella</c> without an <c>org</c>, or a login that recorded none, is not evidence of a mismatch.
    /// </summary>
    public static string? Check(string? contextOrg, string? activeOrgSlug)
    {
        if (string.IsNullOrWhiteSpace(contextOrg) || string.IsNullOrWhiteSpace(activeOrgSlug))
            return null;

        var wanted = contextOrg.Trim();
        var active = activeOrgSlug.Trim();
        if (string.Equals(wanted, active, StringComparison.OrdinalIgnoreCase))
            return null;

        return $"This directory's .bella is for organization '{wanted}', but you are signed in to '{active}'. "
            + $"Run 'bella org switch {wanted}' and try again.";
    }
}
