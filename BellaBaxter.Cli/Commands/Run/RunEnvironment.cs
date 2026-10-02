using System.Collections;
using System.Diagnostics;
using BellaCli.Services;

namespace BellaCli.Commands.Run;

/// <summary>
/// Issue #1049 — the ONE author of the environment <c>bella run</c> gives the command it starts.
/// </summary>
/// <remarks>
/// <para><c>RunCommand</c> used to build it twice — <c>SpawnChild</c> for watch mode and <c>SpawnProcess</c> otherwise —
/// each inheriting the parent's environment and writing every secret over it. A rule added to one copy would have
/// left the other open; <c>RunEnvironmentTests</c> keeps this the only place.</para>
/// <para><b>Reserved names are withheld, not refused.</b> A secret named <c>PATH</c>, <c>LD_PRELOAD</c> or
/// <c>NODE_OPTIONS</c> (<see cref="ReservedEnvironmentNames"/>) would decide what the child loads, not just what it
/// reads. The API refuses such names on write; one stored before that rule is skipped here — the inherited value
/// stays — with a warning naming it, so the run still works and the operator can see why. The explicit opt-in is
/// <c>--allow-reserved-env</c>.</para>
/// </remarks>
internal static class RunEnvironment
{
    /// <summary>
    /// The start info for <paramref name="args"/>: <paramref name="inherited"/> overlaid with <paramref name="secrets"/>,
    /// minus reserved names unless <paramref name="allowReserved"/>. <c>Withheld</c> lists what was skipped.
    /// </summary>
    public static (ProcessStartInfo StartInfo, IReadOnlyList<string> Withheld) BuildStartInfo(
        string[] args,
        IReadOnlyDictionary<string, string> secrets,
        IDictionary inherited,
        bool allowReserved)
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in inherited)
            env[entry.Key?.ToString() ?? ""] = entry.Value?.ToString();

        var withheld = new List<string>();
        foreach (var (name, value) in secrets)
        {
            if (!allowReserved && ReservedEnvironmentNames.Classify(name) is not null)
            {
                withheld.Add(name);
                continue;
            }
            env[name] = value;
        }

        var psi = new ProcessStartInfo { FileName = args[0], UseShellExecute = false };
        for (var i = 1; i < args.Length; i++)
            psi.ArgumentList.Add(args[i]);
        foreach (var (k, v) in env)
            psi.Environment[k] = v;

        withheld.Sort(StringComparer.Ordinal);
        return (psi, withheld);
    }

    /// <summary>The warning for withheld names — one line per name, naming what it would have controlled.</summary>
    public static IEnumerable<string> Warnings(IReadOnlyList<string> withheld) =>
        withheld.Select(name =>
            $"Not injecting secret '{name}': it {ReservedEnvironmentNames.Describe(ReservedEnvironmentNames.Classify(name)!.Value)}, " +
            "which would change what the command loads or runs. The inherited value is kept. " +
            "Pass --allow-reserved-env to inject it anyway.");
}
