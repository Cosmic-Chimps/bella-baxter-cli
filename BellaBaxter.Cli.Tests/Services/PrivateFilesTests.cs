using BellaCli.Infrastructure;

// Every test here skips on Windows (Assert.SkipUnless(OnUnix, …)), which the platform-compatibility
// analyzer cannot see through. The alternative — inlining an OperatingSystem.IsWindows() branch in
// each test — would make the guard invisible as an intent and is not what the sibling
// SvidSocketPathTests does.
#pragma warning disable CA1416 // Unix file-mode APIs; guarded by Assert.SkipUnless(OnUnix)

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// Pilot F3: <c>~/.config/bella-cli</c> was created at 0755 and <c>tokens.json</c>,
/// <c>apikey.json</c>, <c>zke-private-key.dat</c> and the DataProtection key ring were written at
/// 0644 — readable by every other local account on a shared host, a build agent or a container
/// running more than one uid. "They are encrypted" is not an answer while the key ring sits
/// unprotected in the same directory.
///
/// Pattern follows <c>SvidSocketPathTests</c>, including the Windows skip: Unix permission bits do
/// not describe the Windows ACL model, and pretending to apply one would report a safety that was
/// not applied.
/// </summary>
public class PrivateFilesTests
{
    private static bool OnUnix => !OperatingSystem.IsWindows();

    [Fact]
    public void A_new_directory_is_created_owner_only()
    {
        Assert.SkipUnless(OnUnix, "Unix permission bits do not describe the Windows ACL model.");

        var dir = TempDir();
        try
        {
            PrivateFiles.EnsurePrivateDirectory(dir);

            Assert.True(Directory.Exists(dir));
            // Created WITH the mode, not created then chmod'ed: the second version has a window in
            // which the directory exists world-readable, and it is the version that looks fine.
            Assert.Equal(PrivateFiles.DirectoryPermissions, File.GetUnixFileMode(dir));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void An_existing_wide_directory_is_TIGHTENED_not_refused()
    {
        Assert.SkipUnless(OnUnix, "Unix permission bits do not describe the Windows ACL model.");

        // Unlike the SPIFFE socket directory — created fresh each run, where an existing open one is
        // suspicious — this is the operator's own long-lived config directory, created 0755 by every
        // previous version of the CLI. Refusing to start would strand everyone who has ever logged in.
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        File.SetUnixFileMode(
            dir,
            PrivateFiles.DirectoryPermissions | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
        );

        try
        {
            PrivateFiles.EnsurePrivateDirectory(dir);
            Assert.Equal(PrivateFiles.DirectoryPermissions, File.GetUnixFileMode(dir));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void A_written_file_is_owner_only_and_round_trips()
    {
        Assert.SkipUnless(OnUnix, "Unix permission bits do not describe the Windows ACL model.");

        var dir = TempDir();
        try
        {
            PrivateFiles.EnsurePrivateDirectory(dir);
            var file = Path.Combine(dir, "tokens.json");

            PrivateFiles.WritePrivate(file, "ciphertext");

            Assert.Equal(PrivateFiles.FilePermissions, File.GetUnixFileMode(file));
            Assert.Equal("ciphertext", File.ReadAllText(file));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void Overwriting_a_wide_file_tightens_it_first()
    {
        Assert.SkipUnless(OnUnix, "Unix permission bits do not describe the Windows ACL model.");

        // UnixCreateMode applies only on creation, so an existing 0644 file keeps its mode through
        // an overwrite. A re-login must not leave the old permissions in place.
        var dir = TempDir();
        try
        {
            PrivateFiles.EnsurePrivateDirectory(dir);
            var file = Path.Combine(dir, "apikey.json");
            File.WriteAllText(file, "old");
            File.SetUnixFileMode(
                file,
                PrivateFiles.FilePermissions | UnixFileMode.GroupRead | UnixFileMode.OtherRead
            );

            PrivateFiles.WritePrivate(file, "new");

            Assert.Equal(PrivateFiles.FilePermissions, File.GetUnixFileMode(file));
            Assert.Equal("new", File.ReadAllText(file));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void TightenExisting_repairs_the_files_an_older_CLI_left_behind_and_reports_them()
    {
        Assert.SkipUnless(OnUnix, "Unix permission bits do not describe the Windows ACL model.");

        var dir = TempDir();
        try
        {
            PrivateFiles.EnsurePrivateDirectory(dir);
            var keys = Path.Combine(dir, "keys");
            PrivateFiles.EnsurePrivateDirectory(keys);

            var wide = Path.Combine(dir, "tokens.json");
            var wideKeyRing = Path.Combine(keys, "key-abc.xml");
            var alreadyTight = Path.Combine(dir, "apikey.json");

            foreach (var f in new[] { wide, wideKeyRing, alreadyTight })
                File.WriteAllText(f, "x");

            var wideMode = PrivateFiles.FilePermissions | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
            File.SetUnixFileMode(wide, wideMode);
            File.SetUnixFileMode(wideKeyRing, wideMode);
            File.SetUnixFileMode(alreadyTight, PrivateFiles.FilePermissions);

            var repaired = PrivateFiles.TightenExisting(dir);

            // The key ring is included on purpose: it is written by the framework, WritePrivate can
            // never reach it, and on Linux/macOS it is the plaintext key for everything beside it.
            Assert.Contains(wide, repaired);
            Assert.Contains(wideKeyRing, repaired);
            // Only what was actually wider is reported — the notice must not cry wolf every run.
            Assert.DoesNotContain(alreadyTight, repaired);

            Assert.Equal(PrivateFiles.FilePermissions, File.GetUnixFileMode(wide));
            Assert.Equal(PrivateFiles.FilePermissions, File.GetUnixFileMode(wideKeyRing));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void TightenExisting_on_a_directory_that_is_not_there_is_not_an_error()
    {
        // A first run has no credential directory yet, and every command constructs the store.
        Assert.Empty(PrivateFiles.TightenExisting(TempDir()));
    }

    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), $"bella-privfiles-{Guid.NewGuid():N}");

    private static void Cleanup(string dir)
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}

#pragma warning restore CA1416
