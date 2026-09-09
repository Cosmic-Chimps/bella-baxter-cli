using System.Runtime.InteropServices;

namespace BellaCli.Infrastructure;

/// <summary>
/// Owner-only creation and repair for the CLI's credential directory.
///
/// <para>Pilot F3: <c>~/.config/bella-cli</c> and everything under it — <c>tokens.json</c>,
/// <c>apikey.json</c>, <c>zke-private-key.dat</c>, and the DataProtection key ring in
/// <c>keys/</c> — were written with <c>Directory.CreateDirectory</c> and
/// <c>File.WriteAllText</c>, i.e. the process umask: 0755 and 0644. On a shared host, a build
/// agent, a multi-user jump box or any container running more than one uid, every other local
/// account could read them.</para>
///
/// <para>That matters more than "they are encrypted" suggests: <c>AddDataProtection()</c> here is
/// configured with <c>PersistKeysToFileSystem</c> and <b>no</b> <c>ProtectKeysWith*</c>, so on
/// Linux and macOS the key ring is plaintext XML sitting next to the files it protects. Reading
/// both is reading the credential. So THESE PERMISSIONS ARE THE CONTROL for at-rest CLI
/// credentials, not a supporting measure — an OS-protected key ring was considered and DECLINED
/// (backlog §2.23, closed 2026-09-08): 0600 is what stops another local user, and full-disk
/// encryption is what covers a stolen disk. Do not weaken these on the assumption that the
/// encryption underneath them is load-bearing on Unix.</para>
///
/// <para>Windows is a deliberate no-op: its ACL model is not the one these bits describe, and
/// DPAPI does protect the key ring there. Pretending to apply a Unix mode would report a safety
/// that was not applied.</para>
/// </summary>
public static class PrivateFiles
{
    /// <summary>Owner read/write/execute only, so nobody else can even list the directory.</summary>
    public const UnixFileMode DirectoryPermissions =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Owner read/write only.</summary>
    public const UnixFileMode FilePermissions = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>
    /// Creates <paramref name="path"/> owner-only, or tightens it if it already exists wider.
    /// </summary>
    /// <remarks>
    /// Created WITH the mode rather than created and then chmod'ed: the second version has a window
    /// in which the directory exists world-readable, and it is the version that looks fine.
    ///
    /// <para>Unlike <c>SvidSocketPath.PrepareDirectory</c>, an over-permissive existing directory is
    /// tightened rather than refused. There the directory is created fresh each run and an existing
    /// one is suspicious; here it is the operator's own long-lived config directory, created wide by
    /// every previous version of this CLI, and refusing to start would strand everyone who has ever
    /// logged in.</para>
    /// </remarks>
    public static void EnsurePrivateDirectory(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Directory.CreateDirectory(path);
            return;
        }

        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path, DirectoryPermissions);
            return;
        }

        if ((File.GetUnixFileMode(path) & ~DirectoryPermissions) != 0)
            File.SetUnixFileMode(path, DirectoryPermissions);
    }

    /// <summary>
    /// Writes <paramref name="contents"/> to <paramref name="path"/> with owner-only permissions.
    /// </summary>
    /// <remarks>
    /// The mode is set at creation (<c>UnixCreateMode</c>), so a newly written credential is never
    /// briefly world-readable. An existing file keeps its inode — <c>UnixCreateMode</c> applies only
    /// on creation — so it is tightened first.
    /// </remarks>
    public static void WritePrivate(string path, string contents)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            File.WriteAllText(path, contents);
            return;
        }

        if (File.Exists(path))
            TightenFile(path);

        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = FilePermissions,
        };

        using var writer = new StreamWriter(new FileStream(path, options));
        writer.Write(contents);
    }

    /// <summary>
    /// Tightens every file and subdirectory under <paramref name="directory"/>, returning the file
    /// paths that were wider than owner-only.
    /// </summary>
    /// <remarks>
    /// The whole tree, not just the files this class writes. <c>keys/</c> holds the DataProtection
    /// key ring, written by the framework — <see cref="WritePrivate"/> can never reach it, and on
    /// Linux/macOS it is the plaintext key for everything beside it. <c>dek-cache/</c> holds wrapped
    /// DEKs. Every file under this directory is a credential or the CLI's own config; owner-only is
    /// right for all of them, and enumerating the tree means a future writer that forgets
    /// <see cref="WritePrivate"/> is still repaired on the next run.
    ///
    /// <para>Never throws: a credential directory we cannot chmod is not a reason to refuse every
    /// command, and the caller reports what it could not fix.</para>
    /// </remarks>
    public static IReadOnlyList<string> TightenExisting(string directory)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || !Directory.Exists(directory))
            return [];

        List<string> widened = [];

        foreach (var dir in SafeEnumerate(directory, Directory.GetDirectories))
        {
            try
            {
                if ((File.GetUnixFileMode(dir) & ~DirectoryPermissions) != 0)
                    File.SetUnixFileMode(dir, DirectoryPermissions);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        foreach (var file in SafeEnumerate(directory, Directory.GetFiles))
        {
            try
            {
                if ((File.GetUnixFileMode(file) & ~FilePermissions) == 0)
                    continue;

                File.SetUnixFileMode(file, FilePermissions);
                widened.Add(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        return widened;
    }

    private static string[] SafeEnumerate(
        string dir,
        Func<string, string, SearchOption, string[]> get
    )
    {
        try
        {
            return get(dir, "*", SearchOption.AllDirectories);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Sets owner-only on one existing file, ignoring a failure.</summary>
    private static void TightenFile(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        try
        {
            if ((File.GetUnixFileMode(path) & ~FilePermissions) != 0)
                File.SetUnixFileMode(path, FilePermissions);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
