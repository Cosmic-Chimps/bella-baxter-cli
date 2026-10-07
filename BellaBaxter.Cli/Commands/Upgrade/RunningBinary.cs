using System.Reflection;

namespace BellaCli.Commands.Upgrade;

/// <summary>
/// Replacing the executable the process is running from, and what that does to the process itself.
/// </summary>
/// <remarks>
/// <para><b>Why the order matters (#1238).</b> The released CLI is a single-file bundle
/// (<c>PublishSingleFile</c> + <c>EnableCompressionInSingleFile</c>): an assembly that is not loaded yet is
/// read on demand from the bundle AT THE EXECUTABLE'S PATH, at the offset this process recorded at startup.
/// Once <see cref="Replace"/> has run, that path holds a DIFFERENT binary, so the next first-time load fails
/// with <c>FileNotFoundException</c>. A successful upgrade then exited 255 with
/// <c>Could not load file or assembly 'System.IO.Pipelines'</c>: with stdout redirected the success line is
/// JSON, and serializing it was the first use of <c>System.Text.Json</c>'s emitted accessors and of its
/// writer cache — two assemblies never needed before the swap. The command's own <c>catch</c> then tried to
/// report it, as JSON, and failed the same way outside the <c>catch</c>.</para>
///
/// <para>So the rule for the caller is: <see cref="PreloadReferencedAssemblies"/> before the swap, and after a
/// successful one, print and exit — nothing else. A failed swap is safe to report normally, because
/// <see cref="Replace"/> puts the running binary back before it throws.</para>
/// </remarks>
internal static class RunningBinary
{
    /// <summary>
    /// Loads every assembly reachable by reference from the ones already loaded, so that whatever runs after
    /// the swap — the output writer, disposal, the command framework returning — finds its code in memory.
    /// </summary>
    /// <remarks>
    /// A blunt instrument on purpose: listing "the assemblies the success line needs" is exactly the guess
    /// that was wrong, and it changes with the output mode and every package bump. One that cannot load now
    /// (a facade for another platform, an optional dependency that was not shipped) could not have loaded
    /// after the swap either, so it is skipped. Loads by NAME at runtime (<c>Type.GetType("…")</c>) are not
    /// covered; nothing on the success path does that today.
    /// </remarks>
    /// <returns>How many assemblies this call loaded.</returns>
    public static int PreloadReferencedAssemblies()
    {
        var pending = new Queue<Assembly>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.GetName().Name is { } name && seen.Add(name))
                pending.Enqueue(assembly);
        }

        var loaded = 0;
        while (pending.TryDequeue(out var assembly))
        {
            if (assembly.IsDynamic) continue;

            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (reference.Name is null || !seen.Add(reference.Name)) continue;
                try
                {
                    pending.Enqueue(Assembly.Load(reference));
                    loaded++;
                }
                catch
                {
                    // Not in this bundle, so not loadable after the swap either — see the remarks.
                }
            }
        }

        return loaded;
    }

    /// <summary>
    /// Moves <paramref name="replacement"/> over <paramref name="currentExe"/>, keeping the old one as
    /// <c>.bak</c> until the new one is in place.
    /// </summary>
    /// <remarks>
    /// If the second rename fails, the old binary is moved back before the exception propagates: without that,
    /// a failed upgrade left no <c>bella</c> at the path at all — and, for the same single-file reason as above,
    /// a process that could not even print why. The <c>.bak</c> is removed best-effort afterwards; Windows
    /// refuses to delete the image a process is running from, and the next upgrade removes it instead.
    /// </remarks>
    public static void Replace(string currentExe, string replacement)
    {
        var bakFile = currentExe + ".bak";
        if (File.Exists(bakFile)) File.Delete(bakFile);
        File.Move(currentExe, bakFile, overwrite: true);

        try
        {
            File.Move(replacement, currentExe, overwrite: true);
        }
        catch (Exception ex)
        {
            try
            {
                File.Move(bakFile, currentExe, overwrite: true);
            }
            catch (Exception restore)
            {
                throw new IOException(
                    $"{ex.Message} Restoring the previous binary also failed ({restore.Message}); it is at {bakFile}.", ex);
            }
            throw;
        }

        try
        {
            File.Delete(bakFile);
        }
        catch
        {
            // Best effort — see the remarks. The upgrade has happened; a leftover .bak must not report it as failed.
        }
    }
}
