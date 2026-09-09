namespace BellaCli.Infrastructure;

/// <summary>
/// Repairs the permissions of an existing credential directory, once per process.
///
/// <para>Every version of this CLI before the pilot F3 fix created
/// <c>~/.config/bella-cli</c> at 0755 and wrote its files at 0644, so upgrading is not enough —
/// the files already on disk stay readable by every other local account until something tightens
/// them. Both <c>CredentialStore</c> and <c>ZkeService</c> call this; the guard means the operator
/// sees the notice at most once even though both are constructed.</para>
/// </summary>
public static class CredentialDirectory
{
    private static readonly Lock Gate = new();
    private static bool _done;

    public static void TightenOnce(string configDir)
    {
        lock (Gate)
        {
            if (_done)
                return;
            _done = true;
        }

        var tightened = PrivateFiles.TightenExisting(configDir);
        if (tightened.Count == 0)
            return;

        // stderr, not stdout: a `-o json` caller is parsing stdout, and a repair notice is not
        // part of any command's answer.
        Console.Error.WriteLine(
            $"↺ Tightened permissions to owner-only on {tightened.Count} credential file(s) under {configDir}"
        );
    }

    /// <summary>Test seam: lets a test exercise the repair more than once per process.</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
            _done = false;
    }
}
