namespace BellaCli.Infrastructure;

/// <summary>
/// Merges a value given positionally (<c>bella environments get dev</c>) with the same value given
/// as a flag (<c>--environment dev</c>).
///
/// <para>Pilot F8: the five <c>environments</c> subcommands accepted the environment ONLY as a
/// positional argument, and <c>context init</c> accepted its project and environment only
/// positionally — so the documented <c>-e|--environment</c> / <c>-p|--project</c> forms were
/// Spectre parse errors, which read as a broken command rather than as a wrong flag.
/// <c>bella issue</c> already had the convention (<c>-e|--env|--environment</c>); nothing else did.</para>
///
/// <para>Both forms are accepted rather than one being deprecated: scripts in the field use the
/// positional, docs and every other command use the flag. One rule, one place — the write-time
/// merge and any later read of it must never disagree about what was asked for.</para>
/// </summary>
public static class ArgumentMerge
{
    /// <param name="Value">The value to act on; null when neither form was given.</param>
    /// <param name="Error">Set when the two forms contradict each other.</param>
    public readonly record struct Merged(string? Value, string? Error);

    /// <param name="what">What is being named, for the error message (e.g. "environment").</param>
    /// <param name="positional">The positional argument's value.</param>
    /// <param name="option">The flag's value.</param>
    /// <param name="flag">The flag's display form, for the error message (e.g. "--environment").</param>
    public static Merged Resolve(string what, string? positional, string? option, string flag)
    {
        var p = string.IsNullOrWhiteSpace(positional) ? null : positional.Trim();
        var o = string.IsNullOrWhiteSpace(option) ? null : option.Trim();

        if (p is not null && o is not null)
        {
            // Never silently pick one: the operator named two different things and only they know
            // which is the typo. Acting on either is acting on the wrong one.
            if (!string.Equals(p, o, StringComparison.OrdinalIgnoreCase))
                return new Merged(
                    null,
                    $"Conflicting {what}s: '{p}' was given as an argument and '{o}' as {flag}. Pass one."
                );

            return new Merged(p, null);
        }

        return new Merged(p ?? o, null);
    }

    /// <summary>The environment, from <c>[env]</c>/<c>[slug]</c> or <c>-e|--env|--environment</c>.</summary>
    public static Merged Environment(string? positional, string? option) =>
        Resolve("environment", positional, option, "--environment");

    /// <summary>The project, from <c>[project]</c> or <c>-p|--project</c>.</summary>
    public static Merged Project(string? positional, string? option) =>
        Resolve("project", positional, option, "--project");
}
