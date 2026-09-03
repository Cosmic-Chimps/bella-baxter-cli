namespace BellaCli.Services.Spiffe;

// Spec 001 T019 (US1) — parsing `--node k8s:cluster=prod` and `--selector k8s:namespace=payments`.
//
// A separate, pure unit because the FORMAT is genuinely ambiguous and getting it wrong is silent. A
// selector is `type=value`, and the type itself contains a colon (`k8s:namespace`), while the value can
// contain almost anything — including `=` in a base64 fragment or an ARN. So:
//
//   * split on the FIRST `=` only. Splitting on all of them, or on the last, mangles any value that
//     contains one, and the registration then never matches the evidence at attestation time. That
//     failure appears much later, as "attestation refused: missing_selector", with nothing pointing
//     back to the command line that caused it.
//   * do NOT split on `:` — that is part of the type, and treating it as a separator would turn
//     `k8s:namespace=payments` into type `k8s` and lose the rest.
//
// AN EMPTY VALUE IS REFUSED. `--selector k8s:namespace=` would register a selector that can only match
// evidence with an empty namespace, i.e. never — an unattestable workload whose registration looks
// perfectly fine on screen.

/// <summary>One parsed <c>type=value</c> attestation selector.</summary>
/// <param name="Type">The selector type, e.g. <c>k8s:namespace</c>.</param>
/// <param name="Value">The value it must equal.</param>
public sealed record AttestationSelectorArgument(string Type, string Value)
{
    /// <summary>How it is displayed and typed on the command line.</summary>
    public override string ToString() => $"{Type}={Value}";
}

/// <summary>Parses repeated <c>type=value</c> command-line selectors.</summary>
public static class AttestationSelectorParser
{
    /// <summary>Parses one argument, or explains why it cannot be.</summary>
    /// <param name="argument">The raw <c>type=value</c> text.</param>
    /// <param name="flag">Which flag it came from, for the error message.</param>
    /// <param name="error">Set when parsing fails; null on success.</param>
    public static AttestationSelectorArgument? TryParse(string argument, string flag, out string? error)
    {
        error = null;
        var raw = argument?.Trim() ?? string.Empty;

        if (raw.Length == 0)
        {
            error = $"{flag} was given an empty value. Expected {flag} type=value, e.g. "
                + $"{flag} k8s:namespace=payments.";
            return null;
        }

        // FIRST '=' only — see the file header for why the alternatives silently corrupt values.
        var split = raw.IndexOf('=');
        if (split < 0)
        {
            error = $"{flag} '{raw}' is not in type=value form, e.g. {flag} k8s:namespace=payments.";
            return null;
        }

        var type = raw[..split].Trim();
        var value = raw[(split + 1)..].Trim();

        if (type.Length == 0)
        {
            error = $"{flag} '{raw}' has no selector type before the '='.";
            return null;
        }

        if (value.Length == 0)
        {
            // Refused rather than stored: a selector with an empty value can never match any evidence,
            // so the workload would be unattestable while its registration looked correct.
            error = $"{flag} '{raw}' has an empty value. A selector with no value can never match, so "
                + "the workload would be impossible to attest.";
            return null;
        }

        return new AttestationSelectorArgument(type, value);
    }

    /// <summary>Parses every argument, collecting all errors rather than stopping at the first.</summary>
    /// <remarks>
    /// All of them, because a registration command carries several selectors and fixing one typo per
    /// invocation is a poor way to spend an afternoon.
    /// </remarks>
    public static (List<AttestationSelectorArgument> Parsed, List<string> Errors) ParseAll(
        IEnumerable<string> arguments, string flag)
    {
        var parsed = new List<AttestationSelectorArgument>();
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var argument in arguments)
        {
            var selector = TryParse(argument, flag, out var error);
            if (selector is null)
            {
                errors.Add(error!);
                continue;
            }

            // A repeated TYPE with a different value is contradictory: both must match, so the workload
            // could never attest. Refused rather than last-one-wins, which would silently drop the
            // operator's first intent.
            if (!seen.Add(selector.Type))
            {
                var previous = parsed.First(p => p.Type == selector.Type);
                errors.Add(previous.Value == selector.Value
                    ? $"{flag} {selector.Type} was given twice with the same value."
                    : $"{flag} {selector.Type} was given twice with different values "
                      + $"('{previous.Value}' and '{selector.Value}'). Both would have to match, so the "
                      + "workload could never attest.");
                continue;
            }

            parsed.Add(selector);
        }

        return (parsed, errors);
    }
}
