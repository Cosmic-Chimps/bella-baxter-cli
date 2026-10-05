using Microsoft.Kiota.Abstractions;

namespace BellaCli.Services;

/// <summary>
/// #1141 — the API refuses an API key on endpoints that act for a signed-in PERSON (an organization
/// list, switching organization, a person's profile, devices, connector connections) with
/// <c>403</c> and the problem type <c>person-only</c>. This reads that refusal, so a command can say
/// what the operator should do instead of printing a bare HTTP status.
/// </summary>
internal static class PersonOnlyRefusal
{
    /// <summary>The stable code the API sends as the ProblemDetails <c>type</c>.</summary>
    public const string Code = "person-only";

    public const string Message =
        "This command needs an interactive login (bella login), not an API key.";

    /// <summary>
    /// True when <paramref name="ex"/> is the person-only refusal. A client generated before the API
    /// declared the 403 surfaces it as a bare <see cref="ApiException"/> with no body, so a 403 is read
    /// as this refusal only when the caller IS an API key (<paramref name="callerIsApiKey"/>), never
    /// for a person, whose 403 means something else.
    /// </summary>
    public static bool Is(Exception ex, bool callerIsApiKey) => ex switch
    {
        BellaBaxter.Client.Models.ProblemDetails problem =>
            problem.ResponseStatusCode == 403
            && (string.Equals(problem.Type, Code, StringComparison.Ordinal)
                || problem.Type?.EndsWith("/" + Code, StringComparison.Ordinal) == true
                || (string.IsNullOrEmpty(problem.Type) && callerIsApiKey)),
        ApiException api => api.ResponseStatusCode == 403 && callerIsApiKey,
        _ => false,
    };
}
