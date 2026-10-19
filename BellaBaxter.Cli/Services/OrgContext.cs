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
/// <para>This is the rule; <c>ContextService</c> is the only caller of <see cref="Check"/>, and only when the
/// project comes from the directory or session context (an explicit <c>--project</c> is the caller's own
/// statement) and the stored login is the credential being sent. <see cref="CheckExplicitTenant"/> is the same
/// idea for a typed <c>--tenant</c> (#1370), called by <c>run</c>, <c>exec</c> and <c>sdk run</c>.</para>
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

    /// <summary>
    /// #1370 — a <c>--tenant</c> the user TYPED, against the stored login that is about to act. A login carries one
    /// active org and the API takes its tenant from the token alone, so a stored login cannot honour a different
    /// <c>--tenant</c>; carrying on would run the command in the other tenant while the flag said otherwise.
    /// Returns the refusal naming both tenants, or null when they agree or either side is unknown (a login that
    /// recorded no org is not evidence of a mismatch).
    /// </summary>
    /// <remarks>
    /// Applies only when the stored OAuth login is what will be presented (<see cref="BellaClientProvider.UsesStoredLogin"/>
    /// with the workload probe off: in a workload environment the stored login is refused outright by the client
    /// provider, so there is no tenant to disagree about). A stored API key is bound to its own tenant, which is not
    /// recorded locally, so it cannot be compared here.
    /// </remarks>
    public static string? CheckExplicitTenant(string? explicitTenant, CredentialStore credentials)
    {
        if (string.IsNullOrWhiteSpace(explicitTenant))
            return null;
        if (!BellaClientProvider.UsesStoredLogin(credentials, () => false))
            return null;

        var active = credentials.LoadTokens()?.OrgSlug;
        if (string.IsNullOrWhiteSpace(active))
            return null;

        var typed = explicitTenant.Trim();
        if (string.Equals(typed, active.Trim(), StringComparison.OrdinalIgnoreCase))
            return null;

        return $"You asked for tenant '{typed}' (--tenant), but you are signed in to '{active.Trim()}', and a stored login "
            + $"can only act in the tenant it is signed in to. Run 'bella org switch {typed}' and try again.";
    }
}
