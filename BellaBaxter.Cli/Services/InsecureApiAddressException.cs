namespace BellaCli.Services;

/// <summary>
/// The configured Bella API address is plain http to a host other than this machine (backlog §2.31
/// follow-up). The message is operator-facing: it says which source supplied the address and what to do.
/// </summary>
/// <remarks>
/// <para>Deliberately NOT an <see cref="InvalidOperationException"/>. The CLI has ~90
/// <c>catch (InvalidOperationException)</c> blocks around client creation that answer "Not logged in.
/// Run 'bella login' first." — deriving from it turned this refusal into that sentence, which sends the
/// operator to re-authenticate against the very address being refused.</para>
/// <para>Uncaught, Spectre renders it as <c>Error: {message}</c> with a non-zero exit code, never a
/// stack trace.</para>
/// </remarks>
public sealed class InsecureApiAddressException(string message) : Exception(message);
