using System.Text.RegularExpressions;
using BellaCli.Commands.Agent;
using BellaCli.Infrastructure;

// Same reason as PrivateFilesTests: every test here skips on Windows through
// Assert.SkipUnless(OnUnix, …), which the platform-compatibility analyzer cannot see through.
#pragma warning disable CA1416 // Unix file-mode APIs; guarded by Assert.SkipUnless(OnUnix)

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// #832 — the files the CLI writes because an OPERATOR asked for them are owner-only too.
/// </summary>
/// <remarks>
/// <para><c>PrivateFiles.WritePrivate</c> arrived with #618 and was wired only to the CLI's own state
/// under <c>~/.config/bella-cli</c>. The files a person actually asks for — a freshly issued PKI
/// private key, an environment dumped to <c>.env</c>, the sinks <c>bella agent</c> rewrites on every
/// change — went through plain <c>File.WriteAllText</c>, so they were created at the process umask,
/// typically 0644 and readable by every other local account.</para>
///
/// <para>The gap is narrower than "the file is world-readable forever", and worth stating precisely
/// because it is what these tests check: <c>File.WriteAllText</c> truncates an existing inode and
/// keeps its mode, so a file someone had already chmod'ed stayed 0600. It is the FIRST creation that
/// took the umask, and none of the four writers tightened a wide file afterwards the way
/// <c>WritePrivate</c> does. Both halves are asserted below.</para>
/// </remarks>
public class OutputFilesArePrivateTests
{
    private static bool OnUnix => !OperatingSystem.IsWindows();

    private const string Skip = "Unix permission bits do not describe the Windows ACL model.";

    [Theory]
    [InlineData("dotenv")]
    [InlineData("json")]
    [InlineData("yaml")]
    public void An_agent_sink_is_created_owner_only(string type)
    {
        Assert.SkipUnless(OnUnix, Skip);

        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, $"secrets.{type}");

            AgentCommand.WriteAllSinks(
                [new SinkConfig { Type = type, Path = path }],
                new Dictionary<string, string> { ["DATABASE_URL"] = "postgres://u:p@h/db" }
            );

            Assert.True(File.Exists(path));
            Assert.Equal(PrivateFiles.FilePermissions, File.GetUnixFileMode(path));
            Assert.Contains("DATABASE_URL", File.ReadAllText(path));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void An_agent_sink_left_wide_by_an_older_CLI_is_tightened_on_the_next_write()
    {
        Assert.SkipUnless(OnUnix, Skip);

        // The agent rewrites its sinks on every change it detects, so this is the path that actually
        // repairs a long-running deployment: the file already exists, keeps its inode and would keep
        // its 0644 forever if the writer only truncated it.
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "legacy.env");
            File.WriteAllText(path, "OLD=1\n");
            File.SetUnixFileMode(
                path,
                PrivateFiles.FilePermissions | UnixFileMode.GroupRead | UnixFileMode.OtherRead
            );

            AgentCommand.WriteAllSinks(
                [new SinkConfig { Type = "dotenv", Path = path }],
                new Dictionary<string, string> { ["NEW"] = "2" }
            );

            Assert.Equal(PrivateFiles.FilePermissions, File.GetUnixFileMode(path));
            Assert.DoesNotContain("OLD", File.ReadAllText(path));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task The_async_writer_matches_the_synchronous_one()
    {
        Assert.SkipUnless(OnUnix, Skip);

        // `bella pki issue --out` and `bella secrets get --output-file` are async, so they write
        // through WritePrivateAsync. A second implementation is a second chance to get the mode
        // wrong, so it is held to the same two properties.
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        try
        {
            var fresh = Path.Combine(dir, "fresh.key");
            await PrivateFiles.WritePrivateAsync(fresh, "-----BEGIN PRIVATE KEY-----\n");
            Assert.Equal(PrivateFiles.FilePermissions, File.GetUnixFileMode(fresh));

            var wide = Path.Combine(dir, "wide.env");
            File.WriteAllText(wide, "OLD=1\n");
            File.SetUnixFileMode(wide, PrivateFiles.FilePermissions | UnixFileMode.OtherRead);

            await PrivateFiles.WritePrivateAsync(wide, "NEW=2\n");
            Assert.Equal(PrivateFiles.FilePermissions, File.GetUnixFileMode(wide));
            Assert.Equal("NEW=2\n", File.ReadAllText(wide));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void No_command_writes_secret_material_through_the_umask_again()
    {
        // The behavioural tests above can only cover the writers they can call. This names the four
        // call sites the issue found, so a fifth — or a revert of one of these — fails here rather
        // than shipping a 0644 private key that nothing looks at.
        var offenders = new List<string>();

        foreach (
            var (rel, pattern) in new[]
            {
                ("Commands/Agent/AgentCommand.cs", @"File\.WriteAllText\(\s*sink\.Path"),
                (
                    "Commands/Secrets/GetSecretsCommand.cs",
                    @"File\.WriteAllTextAsync\(\s*effectiveOutputFile"
                ),
                ("Commands/Pki/IssuePkiCertCommand.cs", @"File\.WriteAllTextAsync\(\$""\{prefix\}\.key"),
            }
        )
        {
            var source = File.ReadAllText(Path.Combine(RepoRoot(), "BellaBaxter.Cli", rel));
            if (Regex.IsMatch(source, pattern))
                offenders.Add($"{rel} — {pattern}");
        }

        Assert.Empty(offenders);
    }

    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), $"bella-out-{Guid.NewGuid():N}");

    private static void Cleanup(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "BellaBaxter.Cli")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
