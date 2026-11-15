namespace BellaCli.Services;

/// <summary>
/// Spec 108 (FR-018) — the API refused the call with 403 <c>organisation-setup-required</c>: the signed-in person has no
/// organisation yet. Thrown by <see cref="TokenRefreshHandler"/>, the outer handler of every stored-person client, so a
/// command does not print a bare "403" and the person is told what to do and where.
///
/// <para>Derives from <see cref="InvalidOperationException"/> like <see cref="SessionExpiredException"/>, so the many
/// commands that <c>catch (InvalidOperationException ex)</c> and print <c>ex.Message</c> need no change. The message is
/// the API's sentence followed by the link, one line, and <see cref="BellaCli.Infrastructure.CliExceptionHandler"/> prints
/// it on stderr with exit 1 for anything that escapes a command. The CLI never asks for a name and never creates
/// anything.</para>
/// </summary>
public sealed class OrganisationSetupRequiredException : InvalidOperationException
{
    /// <summary>The sentence used when the refusal's body carried none.</summary>
    public const string DefaultDetail = "Finish setting up your organisation in the Bella console.";

    public OrganisationSetupRequiredException(string? detail, string? setupUrl)
        : base(Compose(detail, setupUrl))
    {
        Detail = string.IsNullOrWhiteSpace(detail) ? DefaultDetail : detail;
        SetupUrl = string.IsNullOrWhiteSpace(setupUrl) ? null : setupUrl;
    }

    public string Detail { get; }

    public string? SetupUrl { get; }

    private static string Compose(string? detail, string? setupUrl)
    {
        var sentence = string.IsNullOrWhiteSpace(detail) ? DefaultDetail : detail;
        return string.IsNullOrWhiteSpace(setupUrl) ? sentence : $"{sentence} {setupUrl}";
    }
}
