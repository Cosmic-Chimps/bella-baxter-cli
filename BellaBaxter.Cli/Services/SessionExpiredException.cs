namespace BellaCli.Services;

/// <summary>
/// The stored OAuth session is expired and could not be refreshed. Thrown by
/// <see cref="TokenRefreshHandler"/> so a caller can tell "the credential is dead" apart from
/// "the server is unreachable" without matching on a message
/// (see <see cref="ServerProbe.Classify(Exception)"/>).
///
/// <para>Derives from <see cref="InvalidOperationException"/> and keeps the same message, so the
/// many commands that <c>catch (InvalidOperationException ex)</c> and print <c>ex.Message</c> are
/// unchanged.</para>
/// </summary>
public sealed class SessionExpiredException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);
