using BellaBaxter.Client;
using BellaBaxter.Client.Models;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Spiffe;

// Spec 064 US3 — `bella spiffe audience-readiness`.
//
// The question it answers is the one an operator has to answer BEFORE `set-mode --enforce-audience`: would turning
// enforcement on refuse a workload or CI job that was admitted recently? The answer covers the environment's node
// evidence and every trust domain, and comes from the server (`getAudienceReadiness`), which derives it from
// admissions it already recorded. Nothing is measured or written by asking.
//
// THE EXIT CODE IS THE ANSWER, so a provisioning script can gate on it without parsing output:
//   0  every section is ready
//   1  something would be refused, or a trust domain is critical (no claim rules)
//   2  nothing would be refused, but something is unproven (no evidence yet, or not measured)
//   3  the server could not say (503) or could not be reached
// "No evidence" is never 0: an empty window proves nothing, and treating it as ready is how a switch gets
// discovered by breaking production.

public class SpiffeAudienceReadinessSettings : CommandSettings
{
    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("-e|--environment <SLUG>")]
    public string? Environment { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class SpiffeAudienceReadinessCommand(
    BellaClientProvider provider,
    ContextService context,
    IOutputWriter output)
    : AsyncCommand<SpiffeAudienceReadinessSettings>
{
    private const string Ready = "ready";
    private const string NotReady = "not-ready";

    /// <summary>The exit code for a readiness answer (see the header comment).</summary>
    public static int ExitCodeFor(AudienceReadinessResponse response)
    {
        var sections = new[] { response.NodeEvidence }
            .Concat((response.TrustDomains ?? []).Select(t => t.Section))
            .Where(s => s is not null)
            .ToList();

        if (sections.Any(s => s!.Verdict == NotReady) || (response.TrustDomains ?? []).Any(t => !string.IsNullOrEmpty(t.Critical)))
            return 1;
        // Anything that is not affirmatively "ready" — including a verdict this CLI does not know — is unproven.
        return sections.All(s => s!.Verdict == Ready) ? 0 : 2;
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext ctx, SpiffeAudienceReadinessSettings settings, CancellationToken ct)
    {
        provider.ApplyOutputModeOverrides(settings.Json);

        BellaClient client;
        try
        {
            client = provider.CreateClient();
        }
        catch (InvalidOperationException)
        {
            output.WriteError("Not logged in. Run 'bella login' first.");
            return 3;
        }

        AudienceReadinessResponse? response;
        string projectSlug, envSlug;
        try
        {
            (projectSlug, _, _) = await context.ResolveProjectAsync(settings.Project, client, ct);
            (envSlug, _, _) = await context.ResolveEnvironmentAsync(settings.Environment, projectSlug, client, ct);
            response = await client.Api.V1.Projects[projectSlug].Environments[envSlug]
                .AudienceReadiness.GetAsync(cancellationToken: ct);
        }
        catch (ProblemDetails problem) when (problem.ResponseStatusCode == 503)
        {
            output.WriteError(problem.Detail ?? "Readiness could not be read right now; nothing is known about it.");
            return 3;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            output.WriteError($"Could not read audience readiness: {ex.Message}");
            return 3;
        }

        if (response is null)
        {
            output.WriteError("The API returned no readiness.");
            return 3;
        }

        var exit = ExitCodeFor(response);

        if (settings.Json || output is JsonOutputWriter)
        {
            // A shape of our own, not the Kiota model (its serialiser plumbing would leak into the JSON contract).
            output.WriteObject(new
            {
                project = projectSlug,
                environment = envSlug,
                windowDays = response.WindowDays,
                exitCode = exit,
                nodeEvidence = Shape(response.NodeEvidence),
                trustDomains = (response.TrustDomains ?? []).Select(t => new
                {
                    trustDomainId = t.TrustDomainId,
                    name = t.Name,
                    critical = t.Critical,
                    section = Shape(t.Section),
                }),
            });
            return exit;
        }

        output.WriteInfo($"Token-audience readiness for '{projectSlug}/{envSlug}' over the last {response.WindowDays} days.");
        Render("Node evidence", response.NodeEvidence, critical: null);
        foreach (var td in response.TrustDomains ?? [])
            Render($"Trust domain '{td.Name}'", td.Section, td.Critical);

        switch (exit)
        {
            case 0:
                output.WriteSuccess("Every recent admission presented the expected audience. Enforcing would refuse none of them.");
                break;
            case 1:
                output.WriteWarning("Enforcing now would refuse recent admissions, or a trust domain is critical. See above.");
                break;
            default:
                output.WriteWarning("Nothing shows enforcing is safe yet: some sections have no evidence or are not measured.");
                break;
        }
        return exit;
    }

    private void Render(string title, AudienceReadinessSection? section, string? critical)
    {
        if (section is null)
            return;
        var table = new Table().Border(TableBorder.Rounded).Title(Markup.Escape(title)).AddColumn("").AddColumn("");
        table.AddRow("Verdict", Markup.Escape(section.Verdict ?? "—"));
        table.AddRow("Expected audience", Markup.Escape(string.Join(", ", section.ExpectedAudiences ?? []))
            + (section.IsDefaultAudience == true ? " [yellow](shared default)[/]" : string.Empty));
        table.AddRow("Recommended", Markup.Escape(section.RecommendedAudience ?? "—"));
        table.AddRow("Enforced", section.Enforced == true ? "yes" : "no — observed only");
        table.AddRow("Admissions", $"{section.Matched} matched, {section.NotMatched} did not"
            + (section.Sampled == true ? " (latest admissions only)" : string.Empty));
        if (section.PresentedAudiences is { Count: > 0 } presented)
            table.AddRow("Presented", Markup.Escape(string.Join(", ", presented.Select(p => $"{p.Audience} ({p.Count})"))));
        if (section.NotReadyCallers is { Count: > 0 } callers)
            table.AddRow("Not ready", Markup.Escape(string.Join(", ", callers.Select(c => $"{c.Name} ({c.Count})"))));
        if (critical == "no-claim-rules")
            table.AddRow("[red]Critical[/]", "No claim rules: any workflow on this issuer can obtain a credential. Add a claim rule.");
        AnsiConsole.Write(table);
    }

    private static object? Shape(AudienceReadinessSection? s) => s is null ? null : new
    {
        verdict = s.Verdict,
        expectedAudiences = s.ExpectedAudiences ?? [],
        isDefaultAudience = s.IsDefaultAudience,
        recommendedAudience = s.RecommendedAudience,
        enforced = s.Enforced,
        matched = s.Matched,
        notMatched = s.NotMatched,
        sampled = s.Sampled,
        presentedAudiences = (s.PresentedAudiences ?? []).Select(p => new { audience = p.Audience, count = p.Count }),
        notReadyCallers = (s.NotReadyCallers ?? []).Select(c => new { name = c.Name, count = c.Count }),
    };
}
