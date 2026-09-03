using BellaBaxter.Client;
using BellaBaxter.Client.Models;
using BellaCli.Infrastructure;
using Spectre.Console;

namespace BellaCli.Services;

// spec 020 (T013, US4) — resolves WHERE a secret write lands.
//
// Before this existed, four commands took providerList[0] — the FIRST provider attached to the
// environment. In an environment configured for certificate rotation that list also holds the
// certificate-source, appliance, DNS, and deploy-script providers, so a secret write could be
// aimed at a provider that does not store secrets purely on attachment order.
//
// The rule is: select by MEANING (ProviderMetaType == Secrets), never by position. One
// candidate resolves silently — that keeps every ordinary single-store environment behaving
// exactly as it did. More than one asks the operator. Ambiguity with nobody to ask REFUSES
// rather than guessing (Constitution I — fail closed).

/// <summary>One provider attached to an environment, as far as destination selection cares.</summary>
public sealed record SecretProviderCandidate(string Slug, string Type, string MetaType)
{
    /// <summary>The meta-type that marks a provider as a store for application secrets.</summary>
    public const string SecretsMetaType = "Secrets";

    public bool StoresSecrets =>
        string.Equals(MetaType, SecretsMetaType, StringComparison.OrdinalIgnoreCase);
}

public enum SecretProviderDecisionKind
{
    /// <summary>A single destination was determined; no operator input needed.</summary>
    Resolved,

    /// <summary>Several candidates — the operator must choose.</summary>
    NeedsChoice,

    /// <summary>No safe destination. The caller must refuse the write.</summary>
    Failed,
}

/// <summary>The outcome of deciding a secret write's destination.</summary>
public sealed record SecretProviderDecision(
    SecretProviderDecisionKind Kind,
    string? Slug,
    IReadOnlyList<SecretProviderCandidate> Choices,
    string? Error
)
{
    public static SecretProviderDecision Resolved(string slug) =>
        new(SecretProviderDecisionKind.Resolved, slug, [], null);

    public static SecretProviderDecision NeedsChoice(
        IReadOnlyList<SecretProviderCandidate> choices
    ) => new(SecretProviderDecisionKind.NeedsChoice, null, choices, null);

    public static SecretProviderDecision Failed(string error) =>
        new(SecretProviderDecisionKind.Failed, null, [], error);
}

/// <summary>
/// The destination decision, with no I/O — the whole rule in one testable function.
/// </summary>
public static class SecretProviderSelection
{
    /// <param name="attached">Every provider attached to the environment.</param>
    /// <param name="explicitSlug">A destination the operator named, if any.</param>
    /// <param name="canPrompt">False when no operator can answer (scripted or machine output).</param>
    public static SecretProviderDecision Decide(
        IReadOnlyList<SecretProviderCandidate> attached,
        string? explicitSlug,
        bool canPrompt
    )
    {
        if (!string.IsNullOrWhiteSpace(explicitSlug))
        {
            var named = attached.FirstOrDefault(p =>
                string.Equals(p.Slug, explicitSlug, StringComparison.OrdinalIgnoreCase)
            );

            if (named is null)
            {
                return SecretProviderDecision.Failed(
                    $"Provider '{explicitSlug}' is not attached to this environment. "
                        + $"Attached: {Describe(attached)}"
                );
            }

            // An explicitly named destination is still verified: the flag must not become a
            // new way to aim a write at a provider that cannot store secrets.
            return named.StoresSecrets
                ? SecretProviderDecision.Resolved(named.Slug)
                : SecretProviderDecision.Failed(
                    $"Provider '{named.Slug}' is a {named.Type} provider and does not store "
                        + "secrets. Choose a secrets provider."
                );
        }

        var candidates = attached.Where(p => p.StoresSecrets).ToList();

        return candidates.Count switch
        {
            0 => SecretProviderDecision.Failed(
                attached.Count == 0
                    ? "No providers are attached to this environment. Attach a secrets provider first."
                    : "No secrets provider is attached to this environment — only "
                        + $"{Describe(attached)}. Attach a secrets provider first."
            ),
            // Exactly one: resolve silently. This is what keeps existing single-store
            // environments behaving identically to before this resolver existed.
            1 => SecretProviderDecision.Resolved(candidates[0].Slug),
            _ => canPrompt
                ? SecretProviderDecision.NeedsChoice(candidates)
                : SecretProviderDecision.Failed(
                    "This environment has more than one secrets provider "
                        + $"({string.Join(", ", candidates.Select(c => c.Slug))}). "
                        + "Re-run with --provider <SLUG> to choose one."
                ),
        };
    }

    private static string Describe(IReadOnlyList<SecretProviderCandidate> attached) =>
        attached.Count == 0
            ? "none"
            : string.Join(", ", attached.Select(p => $"{p.Slug} ({p.Type})"));
}

/// <summary>Fetches an environment's providers and turns the decision into a destination slug.</summary>
public class SecretProviderResolver(IOutputWriter output)
{
    /// <summary>
    /// True when there is a human who can answer a prompt. Mirrors the test
    /// <c>SetSecretCommand</c> already applies before prompting for a secret value, so
    /// interactivity behaves consistently across the CLI.
    /// </summary>
    public bool CanPrompt => !Console.IsOutputRedirected && output is not JsonOutputWriter;

    /// <summary>
    /// Resolves the destination provider slug, or null after reporting why it refused.
    /// </summary>
    public async Task<string?> ResolveAsync(
        BellaClient client,
        string projectSlug,
        string envSlug,
        string? explicitSlug,
        CancellationToken ct
    )
    {
        List<EnvironmentProviderResponse> attached;
        try
        {
            attached =
                await client
                    .Api.V1.Projects[projectSlug]
                    .Environments[envSlug]
                    .Providers.GetAsync(cancellationToken: ct)
                    .ConfigureAwait(false) ?? [];
        }
        catch (Exception ex)
        {
            output.WriteError($"Could not read the environment's providers: {ex.Message}");
            return null;
        }

        var candidates = attached
            .Select(p => new SecretProviderCandidate(
                Slug: p.ProviderSlug ?? p.ProviderId ?? string.Empty,
                Type: p.ProviderType ?? "unknown",
                MetaType: p.ProviderMetaType ?? "unknown"
            ))
            .Where(p => !string.IsNullOrWhiteSpace(p.Slug))
            .ToList();

        var decision = SecretProviderSelection.Decide(candidates, explicitSlug, CanPrompt);

        switch (decision.Kind)
        {
            case SecretProviderDecisionKind.Resolved:
                return decision.Slug;

            case SecretProviderDecisionKind.NeedsChoice:
                var chosen = await AnsiConsole
                    .PromptAsync(
                        new SelectionPrompt<string>()
                            .Title("Which secrets provider should this be written to?")
                            .AddChoices(decision.Choices.Select(c => $"{c.Slug} ({c.Type})")),
                        ct
                    )
                    .ConfigureAwait(false);
                return chosen.Split(' ')[0];

            default:
                output.WriteError(decision.Error ?? "Could not determine a destination provider.");
                return null;
        }
    }
}
