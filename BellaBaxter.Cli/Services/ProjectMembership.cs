namespace BellaCli.Services;

/// <summary>Where an implicitly-resolved environment came from.</summary>
public enum EnvironmentSource
{
    /// <summary>The stored API key's own scope, from <c>GET /api/v1/keys/me</c>.</summary>
    ApiKey,

    /// <summary>The directory-scoped <c>.bella</c> file.</summary>
    BellaFile,
}

/// <summary>
/// Pilot F8, the real "<c>--project</c> is ignored": <c>ContextService.ResolveEnvironmentAsync</c>
/// resolved an environment from the API key's scope or from <c>.bella</c> without ever checking it
/// belongs to the project being acted on. So <c>--project B</c> operated on project A's
/// environment, and nothing in the output said so — the most dangerous shape a context bug can
/// take, because the command succeeds.
///
/// <para>The comparison needs no extra call: both sources carry the project alongside the
/// environment. This is the rule; <c>ContextService</c> is the only caller.</para>
/// </summary>
public static class ProjectMembership
{
    /// <summary>
    /// Returns the refusal message, or null when the environment does belong to
    /// <paramref name="requestedProject"/> (or when the source did not say which project it is in,
    /// which is not evidence of a mismatch).
    /// </summary>
    public static string? Check(
        EnvironmentSource source,
        string? environmentsProject,
        string requestedProject,
        string envSlug
    )
    {
        if (string.IsNullOrWhiteSpace(environmentsProject))
            return null;

        if (
            string.Equals(
                environmentsProject.Trim(),
                requestedProject.Trim(),
                StringComparison.OrdinalIgnoreCase
            )
        )
            return null;

        return source switch
        {
            EnvironmentSource.ApiKey =>
                $"API key is scoped to project '{environmentsProject}', not '{requestedProject}'.",
            _ =>
                $"Environment '{envSlug}' from .bella belongs to project '{environmentsProject}', "
                + $"not '{requestedProject}'. Pass -e explicitly.",
        };
    }
}
