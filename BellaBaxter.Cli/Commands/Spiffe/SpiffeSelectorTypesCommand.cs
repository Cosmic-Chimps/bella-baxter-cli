using BellaBaxter.Client;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Spiffe;

// Spec 028 T020 (US1, FR-004) — `bella spiffe selector-types`.
//
// The list comes from the SERVER, every time. The console dialog kept its own list for two years and
// offered `docker:image` and `nomad:dc`, which no attestor has ever verified; a copy in this CLI would
// be that defect in a fourth place. `--help` text is static and cannot call the API, so the `--node` and
// `--selector` option descriptions point here instead of listing types.

public class SpiffeSelectorTypesSettings : CommandSettings
{
    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class SpiffeSelectorTypesCommand(BellaClientProvider provider, IOutputWriter output)
    : AsyncCommand<SpiffeSelectorTypesSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext ctx, SpiffeSelectorTypesSettings settings, CancellationToken ct)
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
            var vocabulary = await client.Api.V1.Spiffe.AttestationVocabulary.GetAsync(cancellationToken: ct);
            var entries = vocabulary?.Entries ?? [];

            if (settings.Json || output is JsonOutputWriter)
            {
                output.WriteObject(new
                {
                    entries = entries.Select(e => new
                    {
                        type = e.Type,
                        scope = e.Scope,
                        evidenceKind = e.Attestor,
                        matchRule = e.MatchRule,
                    }),
                    selfAssertedNotice = vocabulary?.SelfAssertedNotice,
                });
                return 0;
            }

            var table = new Table().Border(TableBorder.Rounded)
                .AddColumn("Type")
                .AddColumn("Usable as")
                .AddColumn("Evidence kind")
                .AddColumn("Match");
            foreach (var e in entries)
            {
                table.AddRow(
                    $"[bold]{e.Type}[/]",
                    e.Scope switch
                    {
                        "node" => "--node",
                        "workload" => "--selector",
                        _ => "--node or --selector",
                    },
                    e.Attestor ?? "—",
                    e.MatchRule == "issuerContains" ? "issuer contains value" : "exact");
            }
            AnsiConsole.Write(table);

            if (!string.IsNullOrWhiteSpace(vocabulary?.SelfAssertedNotice))
                output.WriteInfo(vocabulary.SelfAssertedNotice);

            return 0;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to read the selector vocabulary: {ex.Message}");
            return 1;
        }
    }
}
