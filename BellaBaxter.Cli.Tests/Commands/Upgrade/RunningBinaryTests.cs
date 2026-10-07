using System.Reflection;
using System.Text.Json;
using BellaCli.Commands.Upgrade;

namespace BellaBaxter.Cli.Tests.Commands.Upgrade;

/// <summary>
/// #1238 — a successful <c>bella upgrade</c> exited 255: after the swap, the single-file bundle at the
/// executable's path was a different binary, and the success line needed an assembly not loaded yet.
/// </summary>
/// <remarks>
/// What these cannot prove is the single-file behaviour itself — a test host is not a bundle. That was
/// verified by publishing the CLI single-file and upgrading a copy of it; see the PR for the exit codes.
/// </remarks>
public sealed class RunningBinaryTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("bella-running-binary-");

    public void Dispose() => _dir.Delete(recursive: true);

    private string PathOf(string name) => Path.Combine(_dir.FullName, name);

    [Fact]
    public void The_replacement_takes_the_executables_place_and_leaves_nothing_behind()
    {
        var exe = PathOf("bella");
        var replacement = PathOf("bella.new");
        File.WriteAllText(exe, "old");
        File.WriteAllText(replacement, "new");

        RunningBinary.Replace(exe, replacement);

        Assert.Equal("new", File.ReadAllText(exe));
        Assert.False(File.Exists(replacement));
        Assert.False(File.Exists(exe + ".bak"));
    }

    [Fact]
    public void A_stale_bak_from_an_earlier_upgrade_does_not_block_this_one()
    {
        // On Windows the .bak of the previous upgrade is the image that process was running, so it could
        // not be deleted then. It is removed by the next upgrade, which must not trip over it.
        var exe = PathOf("bella");
        var replacement = PathOf("bella.new");
        File.WriteAllText(exe, "old");
        File.WriteAllText(replacement, "new");
        File.WriteAllText(exe + ".bak", "older");

        RunningBinary.Replace(exe, replacement);

        Assert.Equal("new", File.ReadAllText(exe));
        Assert.False(File.Exists(exe + ".bak"));
    }

    [Fact]
    public void A_replacement_that_cannot_be_moved_into_place_restores_the_running_binary()
    {
        // Before #1238 the first rename had already moved the executable to .bak, so a failure here left
        // no `bella` at the path at all — and a single-file process with nothing at its path cannot even
        // load the code to say why.
        var exe = PathOf("bella");
        File.WriteAllText(exe, "old");

        Assert.ThrowsAny<IOException>(() => RunningBinary.Replace(exe, PathOf("does-not-exist")));

        Assert.Equal("old", File.ReadAllText(exe));
        Assert.False(File.Exists(exe + ".bak"));
    }

    [Fact]
    public void After_preloading_every_reference_of_every_loaded_assembly_is_loaded_or_unloadable()
    {
        // The two assemblies of the reported failure are references of System.Text.Json, which the JSON
        // success line uses: its emitted property accessors and its pooled writer.
        _ = typeof(JsonSerializer);

        // Snapshot BEFORE preloading: other tests run in parallel and may load assemblies afterwards, whose
        // references this call was never asked to cover.
        var before = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).ToList();

        RunningBinary.PreloadReferencedAssemblies();

        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetName().Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("System.IO.Pipelines", loaded);
        Assert.Contains("System.Reflection.Emit.Lightweight", loaded);

        // A reference still absent is acceptable only if it cannot be loaded at all — then it could not
        // have been loaded after the swap either.
        var missed = before
            .SelectMany(a => a.GetReferencedAssemblies())
            .Where(r => r.Name is not null && !loaded.Contains(r.Name) && Loadable(r))
            .Select(r => r.Name)
            .Distinct()
            .ToList();
        Assert.Empty(missed);
    }

    private static bool Loadable(AssemblyName name)
    {
        try
        {
            Assembly.Load(name);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
