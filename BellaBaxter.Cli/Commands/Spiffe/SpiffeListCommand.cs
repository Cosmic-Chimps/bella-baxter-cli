using BellaBaxter.Client;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Spiffe;

// Spec 001 T019 (US1, FR-027) — `bella spiffe list`.
//
// Shows what is registered here, which is the first thing anyone needs when attestation is being
// refused: the commonest cause is a name or an environment that does not match what the agent was
// started with, and both are visible in this table.
//
// REVOKED WORKLOADS ARE SHOWN, not filtered out. A revoked registration still occupies its name, and
// hiding it would make `bella spiffe add` fail with "already exists" for a workload the operator cannot
// see. It is marked rather than omitted.

public class SpiffeListSettings : CommandSettings
{
    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("-e|--environment <SLUG>")]
    public string? Environment { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class SpiffeListCommand(
    BellaClientProvider provider,
    ContextService context,
    IOutputWriter output) : AsyncCommand<SpiffeListSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext ctx, SpiffeListSettings settings, CancellationToken ct)
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
            return 1;
        }

        try
        {
            var (projectSlug, _, _) = await context.ResolveProjectAsync(settings.Project, client, ct);
            var (envSlug, _, _) = await context.ResolveEnvironmentAsync(settings.Environment, projectSlug, client, ct);

            var list = await client.Api.V1.Projects[projectSlug].Environments[envSlug]
                .WorkloadIdentities.GetAsync(cancellationToken: ct);

            var items = list?.Items ?? [];

            if (settings.Json || output is JsonOutputWriter)
            {
                output.WriteObject(new
                {
                    project = projectSlug,
                    environment = envSlug,
                    total = list?.Total ?? items.Count,
                    workloads = items.Select(w => new
                    {
                        name = w.Name,
                        spiffeId = w.SpiffeId,
                        id = w.Id,
                        status = w.Status,
                        revokedAt = w.RevokedAt,
                        issuedTokenTtlMinutes = w.IssuedTokenTtlMinutes,
                        nodeAttestors = (w.NodeAttestors ?? []).Select(s => $"{s.Type}={s.Value}"),
                        selectors = (w.Selectors ?? []).Select(s => $"{s.Type}={s.Value}"),
                        constraintReport = SpiffeAddCommand.ReportJson(w.ConstraintReport?.ConstraintReportDto),
                    }),
                });
                return 0;
            }

            if (items.Count == 0)
            {
                output.WriteInfo(
                    $"No workload identities registered in {projectSlug}/{envSlug}. "
                    + "Register one with 'bella spiffe add --name <name>'.");
                return 0;
            }

            var table = new Table().Border(TableBorder.Rounded)
                .AddColumn("Name")
                .AddColumn("SPIFFE ID")
                .AddColumn("Node attestors")
                .AddColumn("Selectors")
                .AddColumn("TTL");

            foreach (var w in items)
            {
                var revoked = w.RevokedAt is not null
                    || string.Equals(w.Status, "Revoked", StringComparison.OrdinalIgnoreCase);

                table.AddRow(
                    revoked ? $"[dim]{w.Name} (revoked)[/]" : w.Name ?? "—",
                    revoked ? $"[dim]{w.SpiffeId}[/]" : w.SpiffeId ?? "—",
                    Describe(w.NodeAttestors, w.ConstraintReport?.ConstraintReportDto?.NodeAttestors),
                    Describe(w.Selectors, w.ConstraintReport?.ConstraintReportDto?.Selectors),
                    $"{w.IssuedTokenTtlMinutes} min");
            }

            AnsiConsole.Write(table);
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            output.WriteError(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to list workload identities: {ex.Message}");
            return 1;
        }
    }

    // Spec 028 (FR-007): ✓ = an attestor verifies it; ~ = self-asserted, a configuration check only.
    // Without a report (an older server) the plain form is shown, never a guessed verdict.
    private static string Describe(
        List<BellaBaxter.Client.Models.AttestationSelectorDto>? selectors,
        List<BellaBaxter.Client.Models.ConstraintVerdictDto>? verdicts) =>
        selectors is null || selectors.Count == 0
            // Flagged rather than blank: nothing here means Strict mode has nothing to verify beyond
            // the environment-wide bootstrap token.
            ? "[yellow]none[/]"
            : string.Join("\n", selectors.Select(s =>
            {
                var verdict = verdicts?.FirstOrDefault(v => v.Type == s.Type && v.Value == s.Value)?.Verification;
                return verdict switch
                {
                    "verified" => $"[green]✓[/] {s.Type}={s.Value}",
                    "self-asserted" => $"[yellow]~[/] {s.Type}={s.Value} [dim](self-asserted)[/]",
                    _ => $"{s.Type}={s.Value}",
                };
            }));
}
