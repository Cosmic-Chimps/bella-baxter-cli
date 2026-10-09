namespace BellaCli.Services;

/// <summary>
/// Advisory clients-1 — a credential would have been sent to a server it does not belong to, and was
/// not. The message is operator-facing: it names both servers, where the address came from, and what to
/// run.
/// </summary>
/// <remarks>
/// Deliberately NOT an <see cref="InvalidOperationException"/>, for the reason
/// <see cref="InsecureApiAddressException"/> gives: the CLI's many <c>catch (InvalidOperationException)</c>
/// blocks answer "Not logged in", which would hide that the refusal is about the ADDRESS.
/// </remarks>
public sealed class CredentialOriginException(string message) : Exception(message);
