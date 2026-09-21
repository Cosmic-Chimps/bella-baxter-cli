using System.ComponentModel;
using BellaBaxter.Client;
using BellaBaxter.Client.Models;
using BellaCli.Commands.Secrets;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Totp;

public class ImportTotpSettings : CommandSettings
{
    [CommandArgument(0, "<name>")]
    public string Name { get; init; } = "";

    /// <summary>
    /// The otpauth:// URL, inline. Issue #833: it carries the TOTP SEED, so it is visible in
    /// <c>ps</c> for the life of the process and lands in shell history — the same argument #743
    /// made about a secret's value. Kept as the convenient interactive form, and because removing
    /// it would break existing scripts; now OPTIONAL so there is somewhere better to go.
    /// </summary>
    [CommandArgument(1, "[otpauth-url]")]
    [Description("The otpauth:// URL. Carries the seed — visible in `ps` and shell history; prefer --stdin")]
    public string? OtpauthUrl { get; init; }

    /// <summary>Read the URL from stdin: <c>printf %s "$URL" | bella totp import NAME --stdin</c>.</summary>
    [CommandOption("--stdin")]
    [Description("Read the otpauth:// URL from stdin. The way to import from a script or CI")]
    public bool Stdin { get; init; }

    /// <summary>Read the URL from a file, bytes as they are.</summary>
    [CommandOption("--from-file <PATH>")]
    [Description("Read the otpauth:// URL from a file")]
    public string? FromFile { get; init; }

    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("-e|--env|--environment <SLUG>")]
    public string? Environment { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class ImportTotpCommand(BellaClientProvider provider, ContextService context, IOutputWriter output)
    : AsyncCommand<ImportTotpSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext ctx, ImportTotpSettings settings, CancellationToken ct)
    {
        provider.ApplyOutputModeOverrides(settings.Json);

        // Issue #833: settle WHERE the URL comes from before anything else, reusing #743's rule
        // rather than restating it — a second copy of "which sources may be combined" is how the
        // two commands come to disagree about the same question.
        if (
            !SecretValueSource.TrySelect(
                settings.OtpauthUrl,
                settings.Stdin,
                settings.FromFile,
                out var urlSource,
                out var sourceError
            )
        )
        {
            output.WriteError(sourceError!);
            return 1;
        }

        string? otpauthUrl = null;
        switch (urlSource)
        {
            case SecretValueSource.Kind.Stdin:
                otpauthUrl = (await SecretValueSource.ReadStdinAsync(ct)).Trim();
                if (otpauthUrl.Length == 0)
                {
                    output.WriteError("--stdin was given but stdin was empty; nothing was imported.");
                    return 1;
                }
                break;

            case SecretValueSource.Kind.File:
                if (!File.Exists(settings.FromFile))
                {
                    output.WriteError($"File not found: {settings.FromFile}");
                    return 1;
                }
                otpauthUrl = (await SecretValueSource.ReadFileAsync(settings.FromFile!, ct)).Trim();
                if (otpauthUrl.Length == 0)
                {
                    output.WriteError($"'{settings.FromFile}' is empty; nothing was imported.");
                    return 1;
                }
                break;

            case SecretValueSource.Kind.Positional:
                otpauthUrl = settings.OtpauthUrl!;
                // Said once, where the operator can still act on it, and only on a terminal — in a
                // pipeline the advice is unreadable and the shell history it warns about does not
                // exist. Same placement as `secrets set`.
                if (Interactivity.IsInteractive(output))
                {
                    output.WriteWarning(
                        "This otpauth:// URL contains the TOTP seed. It is now in your shell history "
                            + "and was visible in `ps` while the command ran. For automation use "
                            + "--stdin or --from-file."
                    );
                }
                break;

            case SecretValueSource.Kind.Prompt:
                if (!Interactivity.IsInteractive(output))
                {
                    output.WriteError(
                        "The otpauth:// URL is required in non-interactive mode. Pass it with "
                            + "--stdin, --from-file <path>, or as an argument."
                    );
                    return 1;
                }
                otpauthUrl = await AnsiConsole.PromptAsync(
                    new TextPrompt<string>($"otpauth:// URL for [bold]{settings.Name}[/]:")
                        .Secret()
                        .ClearOnFinish(),
                    ct
                );
                break;
        }

        if (otpauthUrl is null || !otpauthUrl.StartsWith("otpauth://"))
        {
            output.WriteError("Invalid OTP auth URL. Must start with 'otpauth://'.");
            return 1;
        }

        BellaClient client;
        try { client = provider.CreateClient(); }
        catch (InvalidOperationException)
        {
            output.WriteError("Not logged in. Run 'bella login' first.");
            return 1;
        }

        try
        {
            var (projectSlug, _, _) = await context.ResolveProjectAsync(settings.Project, client, ct);
            var (envSlug, envName, _) = await context.ResolveEnvironmentAsync(settings.Environment, projectSlug, client, ct);

            TotpKeyImportResponse? result = null;
            await output.StatusAsync(
$"Importing TOTP key '{settings.Name}'...",
async () =>
            {
                result = await client.Api.V1.Projects[projectSlug].Environments[envSlug].Totp.Import
                    .PostAsync(new ImportTotpKeyRequest { Name = settings.Name, OtpauthUrl = otpauthUrl },
                        cancellationToken: ct);
            });

            if (output is JsonOutputWriter)
            {
                output.WriteObject(new { name = result?.Name, otpauthUrl = result?.OtpauthUrl, environment = envName });
                return 0;
            }

            output.WriteSuccess($"TOTP key '{result?.Name ?? settings.Name}' imported into {envName}.");
            if (!string.IsNullOrEmpty(result?.QrCodeBase64))
                AnsiConsole.MarkupLine("[dim]QR code available in the web console.[/]");
            return 0;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to import TOTP key: {ex.Message}");
            return 1;
        }
    }
}
