using BellaBaxter.Client.Models;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Text.Json;

namespace BellaCli.Commands.Usage;

public class UsageCommand(BellaClientProvider provider, CredentialStore credentials, IOutputWriter output)
    : AsyncCommand<UsageCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandOption("--json")]
        [Description("Output usage data as JSON")]
        public bool Json { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        if (!credentials.IsAuthenticated())
        {
            output.WriteError("Not logged in. Run 'bella login' to authenticate.", "unauthenticated");
            return 1;
        }

        TenantUsageResponse? usage = null;

        try
        {
            var client = provider.CreateClient();
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Fetching usage...", async _ =>
                {
                    usage = await client.Api.V1.Tenant.Usage.GetAsync(cancellationToken: ct);
                });
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to fetch usage: {ex.Message}", "api_error");
            return 1;
        }

        if (usage is null)
        {
            output.WriteError("No usage data returned.", "empty_response");
            return 1;
        }

        if (settings.Json || output is not HumanOutputWriter)
        {
            output.WriteObject(new
            {
                plan = usage.Plan,
                currentMonth = usage.CurrentMonth,
                isOperatorManaged = usage.IsOperatorManaged,
                isUnlimited = usage.IsUnlimited,
                hasActiveSubscription = usage.HasActiveSubscription,
                requestsUsed = usage.RequestsUsed,
                requestsRemaining = usage.RequestsRemaining,
                freeMonthlyQuota = usage.FreeMonthlyQuota,
                overageRatePerRequest = usage.OverageRatePerRequest,
                estimatedOverageCost = usage.EstimatedOverageCost,
            });
            return 0;
        }

        RenderHuman(usage);
        return 0;
    }

    private static void RenderHuman(TenantUsageResponse u)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]📊 API Usage[/]");
        AnsiConsole.MarkupLine("[dim]" + new string('─', 60) + "[/]");

        // Plan badge
        var planColor = u.Plan?.ToLowerInvariant() switch
        {
            "enterprise" => "purple",
            "payasyougo" or "pay_as_you_go" => "cyan",
            _ => "grey"
        };
        var planLabel = u.Plan ?? "Unknown";
        AnsiConsole.MarkupLine($"[white]Plan:[/]          [{planColor}]{Markup.Escape(planLabel)}[/]");
        AnsiConsole.MarkupLine($"[white]Month:[/]         [dim]{Markup.Escape(u.CurrentMonth ?? "—")}[/]");

        // Subscription / managed status
        if (u.IsOperatorManaged == true)
        {
            AnsiConsole.MarkupLine("[white]Managed by:[/]    [green]Operator (unlimited)[/]");
        }
        else if (u.IsUnlimited == true)
        {
            AnsiConsole.MarkupLine("[white]Requests:[/]      [green]Unlimited (Enterprise)[/]");
        }
        else
        {
            var subStatus = u.HasActiveSubscription == true
                ? "[green]Active[/]"
                : "[yellow]No active subscription (free tier)[/]";
            AnsiConsole.MarkupLine($"[white]Subscription:[/] {subStatus}");

            AnsiConsole.WriteLine();

            var used = (long)(u.RequestsUsed ?? 0);
            var quota = (long)(u.FreeMonthlyQuota ?? 2000);
            var remaining = Math.Max(0L, (long)(u.RequestsRemaining ?? 0));
            var overage = Math.Max(0L, used - quota);

            // Progress bar
            var chart = new BreakdownChart()
                .Width(50)
                .AddItem("Used", used <= quota ? used : quota, Color.Green)
                .AddItem("Remaining", remaining, Color.Grey)
                .AddItem("Overage", overage, Color.Red);

            AnsiConsole.Write(chart);
            AnsiConsole.WriteLine();

            AnsiConsole.MarkupLine($"[white]Requests used:[/]      [bold]{used:N0}[/] / [dim]{quota:N0}[/] free");

            if (overage > 0)
            {
                AnsiConsole.MarkupLine($"[white]Overage:[/]            [red bold]{overage:N0} requests[/]");
                var cost = u.EstimatedOverageCost ?? 0d;
                var rate = u.OverageRatePerRequest ?? 0.005d;
                AnsiConsole.MarkupLine($"[white]Overage rate:[/]       [dim]${rate:F4} / request[/]");
                AnsiConsole.MarkupLine($"[white]Estimated extra cost:[/] [red]${cost:F2}[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[white]Remaining:[/]          [green]{remaining:N0} requests[/]");
            }
        }

        AnsiConsole.MarkupLine("[dim]" + new string('─', 60) + "[/]");
        AnsiConsole.WriteLine();
    }
}
