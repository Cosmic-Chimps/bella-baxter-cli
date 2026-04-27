namespace BellaCli.Commands;

/// <summary>
/// Holds the raw passthrough arguments that were stripped from the CLI args array
/// before Spectre.Console's parser runs. This is necessary because Spectre.Console
/// crashes on single-dash multi-character flags like <c>-auto-approve</c> even when
/// they appear after the <c>--</c> separator.
/// </summary>
/// <remarks>
/// Populated in <c>Program.cs</c> before <c>app.RunAsync(args)</c>.
/// Consumed in <c>CollectArgs</c> / <c>MergeArgs</c> inside each passthrough command.
/// </remarks>
internal static class PassthroughArgsHolder
{
    private static string[] _args = [];

    internal static void Set(string[] args) => _args = args;

    internal static string[] Get() => _args;
}
