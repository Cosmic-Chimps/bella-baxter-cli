using BellaCli.Commands.Sdk;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Exec;

/// <summary>
/// Deprecated shim for 'bella exec'. Prints a deprecation notice and delegates to SdkRunCommand.
/// Use 'bella sdk run' instead.
/// </summary>
public class DeprecatedExecCommand(
    CredentialStore credentials,
    ConfigService config,
    AuthService authService,
    WorkloadIdentityService workloadIdentity,
    ZkeService zke,
    IOutputWriter output
) : SdkRunCommand(credentials, config, authService, workloadIdentity, zke, output)
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        Settings settings,
        CancellationToken ct
    )
    {
        AnsiConsole.MarkupLine(
            "[yellow]⚠  'bella exec' is deprecated and will be removed in a future release.[/]"
        );
        AnsiConsole.MarkupLine(
            "[yellow]   Use [bold]bella sdk run[/] instead — it does the same thing.[/]"
        );
        AnsiConsole.WriteLine();

        return await base.ExecuteAsync(context, settings, ct);
    }
}
