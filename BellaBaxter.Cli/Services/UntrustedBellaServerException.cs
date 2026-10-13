namespace BellaCli.Services;

/// <summary>
/// Advisory clients-1 follow-up (cli-bella-url), security team 2026-10-10 — the server a working-directory
/// <c>.bella</c> names is not one the user has trusted, so nothing was sent to it. Operator-facing message.
/// </summary>
/// <remarks>
/// Deliberately NOT an <see cref="InvalidOperationException"/>, for the reason
/// <see cref="CredentialOriginException"/> gives: the CLI's <c>catch (InvalidOperationException)</c> blocks
/// answer "Not logged in", which would hide that the refusal is about the ADDRESS.
/// </remarks>
public sealed class UntrustedBellaServerException(string message) : Exception(message);

/// <summary>What the operator is shown when asked to trust a server a <c>.bella</c> names.</summary>
public sealed record UntrustedBellaServer(string Origin, string BellaFile, string ConfiguredServer);
