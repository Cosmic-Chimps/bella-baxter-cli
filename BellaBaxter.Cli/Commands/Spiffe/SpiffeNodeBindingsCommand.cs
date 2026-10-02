using BellaBaxter.Client;
using BellaBaxter.Client.Models;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Microsoft.Kiota.Abstractions;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Spiffe;

// Spec 065 (US3) — `bella spiffe node-bindings` and `bella spiffe node-bindings release <id>`.
//
// An EC2 instance that attests with AWS evidence is BOUND to its workload on first use, and must present the
// re-attestation credential it was given on every later attestation. Trust on first use has one weakness: the first
// attestation wins. This listing is where an operator notices a binding they did not make, and release is how they
// recover from it, or from an agent that lost its state directory.
//
// RELEASED BINDINGS ARE SHOWN, marked, like revoked workloads in `bella spiffe list`: "who released this instance,
// and when" keeps an answer.

public class SpiffeNodeBindingsSettings : CommandSettings
{
    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("-e|--environment <SLUG>")]
    public string? Environment { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public sealed class SpiffeNodeBindingsReleaseSettings : SpiffeNodeBindingsSettings
{
    [CommandArgument(0, "<ID>")]
    [System.ComponentModel.Description("The binding id, from `bella spiffe node-bindings`.")]
    public Guid Id { get; init; }

    [CommandOption("--force")]
    [System.ComponentModel.Description("Release without asking for confirmation.")]
    public bool Force { get; init; }
}

public class SpiffeNodeBindingsCommand(
    BellaClientProvider provider,
    ContextService context,
    IOutputWriter output) : AsyncCommand<SpiffeNodeBindingsSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext ctx, SpiffeNodeBindingsSettings settings, CancellationToken ct)
    {
        provider.ApplyOutputModeOverrides(settings.Json);
        if (!TryClient(provider, output, out var client))
            return 1;

        try
        {
            var (projectSlug, _, _) = await context.ResolveProjectAsync(settings.Project, client, ct);
            var (envSlug, _, _) = await context.ResolveEnvironmentAsync(settings.Environment, projectSlug, client, ct);

            var list = await client.Api.V1.Projects[projectSlug].Environments[envSlug].NodeBindings
                .GetAsync(cancellationToken: ct);
            var items = list?.Items ?? [];

            if (settings.Json || output is JsonOutputWriter)
            {
                output.WriteObject(new
                {
                    project = projectSlug,
                    environment = envSlug,
                    total = list?.Total ?? items.Count,
                    bindings = items.Select(Json),
                });
                return 0;
            }

            if (items.Count == 0)
            {
                output.WriteInfo(
                    $"No EC2 instance has attested with AWS evidence in {projectSlug}/{envSlug}. "
                    + "An instance is bound to its workload the first time it attests.");
                return 0;
            }

            var table = new Table().Border(TableBorder.Rounded)
                .AddColumn("Id")
                .AddColumn("Instance")
                .AddColumn("Account / Region")
                .AddColumn("Workload")
                .AddColumn("Bound")
                .AddColumn("Last attested")
                .AddColumn("Last admission");

            foreach (var b in items)
            {
                string Dim(string? value) => b.Released == true
                    ? $"[dim]{Markup.Escape(value ?? "—")}[/]"
                    : Markup.Escape(value ?? "—");

                table.AddRow(
                    Dim(b.Id?.ToString()),
                    b.Released == true
                        ? $"[dim]{Markup.Escape(b.InstanceId ?? "—")} (released: {Markup.Escape(b.ReleaseReason ?? "?")})[/]"
                        : Markup.Escape(b.InstanceId ?? "—"),
                    Dim($"{b.Account} / {b.Region}"),
                    Dim(b.WorkloadName),
                    Dim(b.BoundAt?.ToString("u")),
                    Dim(b.LastAttestedAt?.ToString("u")),
                    Dim(b.LastAdmission));
            }

            AnsiConsole.Write(table);
            output.WriteInfo(
                "A binding you did not expect means another attestation claimed that instance first. "
                + "Release it with 'bella spiffe node-bindings release <id>'.");
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            output.WriteError(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to list node bindings: {ex.Message}");
            return 1;
        }
    }

    internal static object Json(NodeBindingResponse b) => new
    {
        id = b.Id,
        instanceId = b.InstanceId,
        account = b.Account,
        region = b.Region,
        workloadIdentityId = b.WorkloadIdentityId,
        workloadName = b.WorkloadName,
        boundAt = b.BoundAt,
        lastAttestedAt = b.LastAttestedAt,
        lastAdmission = b.LastAdmission,
        released = b.Released ?? false,
        releasedAt = b.ReleasedAt,
        releaseReason = b.ReleaseReason,
    };

    internal static bool TryClient(BellaClientProvider provider, IOutputWriter output, out BellaClient client)
    {
        try
        {
            client = provider.CreateClient();
            return true;
        }
        catch (InvalidOperationException)
        {
            output.WriteError("Not logged in. Run 'bella login' first.");
            client = null!;
            return false;
        }
    }
}

public class SpiffeNodeBindingsReleaseCommand(
    BellaClientProvider provider,
    ContextService context,
    IOutputWriter output) : AsyncCommand<SpiffeNodeBindingsReleaseSettings>
{
    /// <summary>What a release does, said before it is done: it re-runs trust on first use.</summary>
    public const string Consequence =
        "The next valid identity document for this instance binds afresh, to whichever workload attests first.";

    protected override async Task<int> ExecuteAsync(
        CommandContext ctx, SpiffeNodeBindingsReleaseSettings settings, CancellationToken ct)
    {
        provider.ApplyOutputModeOverrides(settings.Json);
        if (!SpiffeNodeBindingsCommand.TryClient(provider, output, out var client))
            return 1;

        try
        {
            var (projectSlug, _, _) = await context.ResolveProjectAsync(settings.Project, client, ct);
            var (envSlug, _, _) = await context.ResolveEnvironmentAsync(settings.Environment, projectSlug, client, ct);

            if (!settings.Force)
            {
                if (!Interactivity.IsInteractive(output))
                {
                    output.WriteError("Use --force to release without confirmation.");
                    return 1;
                }

                AnsiConsole.MarkupLine($"About to release binding [bold]{settings.Id}[/] in [bold]{projectSlug}/{envSlug}[/].");
                AnsiConsole.MarkupLine($"  {Consequence}");
                if (!AnsiConsole.Confirm("Release?", defaultValue: false))
                {
                    output.WriteInfo("Cancelled.");
                    return 0;
                }
            }

            await client.Api.V1.Projects[projectSlug].Environments[envSlug].NodeBindings[settings.Id]
                .DeleteAsync(cancellationToken: ct);

            if (settings.Json || output is JsonOutputWriter)
            {
                output.WriteObject(new { project = projectSlug, environment = envSlug, id = settings.Id, released = true });
                return 0;
            }

            output.WriteSuccess($"Binding {settings.Id} released. {Consequence}");
            return 0;
        }
        catch (ApiException ex) when (ex.ResponseStatusCode == 404)
        {
            // Missing, already released, in another environment, or not yours to release: the server answers all
            // four the same way on purpose, so this does not guess which.
            output.WriteError($"No such active binding in this environment: {settings.Id}.");
            return 1;
        }
        catch (InvalidOperationException ex)
        {
            output.WriteError(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to release the binding: {ex.Message}");
            return 1;
        }
    }
}
