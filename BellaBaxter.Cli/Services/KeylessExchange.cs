namespace BellaCli.Services;

/// <summary>The three slugs a keyless exchange must name to find the trust domain it is for.</summary>
public enum SlugField
{
    Tenant,
    Project,
    Environment,
}

/// <summary>
/// What <see cref="WorkloadIdentityService.TryAutoExchangeAsync"/> found. A bare <c>null</c> used to stand for all
/// of these, which is how a job that had only forgotten its context was told "Not logged in" (#1370).
/// </summary>
public abstract record KeylessExchange
{
    private KeylessExchange() { }

    /// <summary>Not a workload platform, a Bella key is already in the environment, or no OIDC token was obtained.</summary>
    public sealed record NotApplicable : KeylessExchange;

    /// <summary>
    /// An exchange would have been attempted, but tenant, project or environment is still empty after resolution.
    /// The exchange endpoint was NOT called.
    /// </summary>
    public sealed record MissingContext(WorkloadPlatform Platform, IReadOnlyList<SlugField> Missing) : KeylessExchange
    {
        /// <summary>
        /// The refusal, naming each missing field and how to supply it. The flags, variables and <c>.bella</c> keys
        /// are the ones <see cref="WorkloadIdentityService.ResolveSlugs"/> really reads. Only what is missing is
        /// listed, so a reader never wonders whether the field they did give is also a problem.
        /// </summary>
        /// <param name="includeLoginHint">
        /// Say that a stored login is the other way in. False where there is no such alternative
        /// (<c>bella auth oidc</c> exists to get a key; it has no login to fall back to).
        /// </param>
        public string Describe(bool includeLoginHint)
        {
            var lines = new List<string>();
            var names = new List<string>();
            foreach (var field in Missing)
            {
                var (name, how) = field switch
                {
                    SlugField.Tenant => ("tenant", "--tenant <slug>, BELLA_BAXTER_TENANT, or `org` in .bella"),
                    SlugField.Project => ("project", "--project <slug>, BELLA_BAXTER_PROJECT, or `project` in .bella"),
                    _ => ("environment", "--env <slug>, BELLA_BAXTER_ENV, or `environment` in .bella"),
                };
                names.Add(name);
                lines.Add($"  {name}: {how}");
            }

            var subject = Platform == WorkloadPlatform.None
                ? "The OIDC token exchange"
                : $"Keyless sign-in on {Platform}";
            var message =
                $"{subject} needs a tenant, project and environment to find the trust domain, and is missing: "
                + $"{string.Join(", ", names)}.\n"
                + string.Join("\n", lines);

            if (Missing.Contains(SlugField.Project) || Missing.Contains(SlugField.Environment))
                message += "\nA .bella file supplies the project and environment together.";
            if (includeLoginHint)
                message += "\nOr run 'bella login' to use a stored login instead.";
            return message;
        }
    }

    /// <summary>The server issued a short-lived key.</summary>
    public sealed record Exchanged(OidcExchangeResult Key) : KeylessExchange;

    /// <summary>The exchange was attempted and rejected, or could not be completed.</summary>
    public sealed record Failed(OidcExchangeRefusal? Refusal = null) : KeylessExchange;
}
