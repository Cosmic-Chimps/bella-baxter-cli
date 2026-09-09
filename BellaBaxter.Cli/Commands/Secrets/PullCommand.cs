using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Secrets;

/// <summary>
/// Shorthand for <c>bella secrets get -o .env</c>.
/// Downloads all secrets for the current project/environment and writes them
/// to a file (default: <c>.env</c>).
/// </summary>
public class PullCommand(
    BellaClientProvider provider,
    ContextService context,
    IOutputWriter output,
    ZkeClientSelection zkeSelection
) : AsyncCommand<GetSecretsSettings>
{
    // pull IS get, written to a file — so it inherits the ZKE gate for free rather than growing a
    // fifth copy of the client-selection block (research R12).
    private readonly GetSecretsCommand _inner = new(provider, context, output, zkeSelection);

    protected override Task<int> ExecuteAsync(
        CommandContext ctx,
        GetSecretsSettings settings,
        CancellationToken ct
    ) => _inner.RunAsync(settings, settings.OutputFile ?? ".env", ct);
}
