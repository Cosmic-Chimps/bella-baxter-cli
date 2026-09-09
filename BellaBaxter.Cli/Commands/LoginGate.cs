namespace BellaCli.Commands;

/// <summary>
/// What <c>bella login</c> should do given the state of the stored credentials.
/// </summary>
public enum LoginAction
{
    /// <summary>A usable credential is already stored — warn and exit 0.</summary>
    AlreadyLoggedIn,

    /// <summary>An OAuth session is stored but expired — try a silent refresh first.</summary>
    TryRefresh,

    /// <summary>Nothing usable is stored (or <c>--force</c>) — run the login flow.</summary>
    StartLogin,
}

/// <summary>
/// Pilot F7: <c>bella login</c> gated on <c>CredentialStore.IsAuthenticated()</c>, which only means
/// "a credential file exists and decrypts" — expiry was never consulted. An expired session
/// therefore printed "Already logged in. Use --force to re-authenticate." and exited 0, while every
/// subsequent command failed through <c>TokenRefreshHandler</c> with "Run 'bella login'". The
/// operator's only escape was a flag nothing told them about.
///
/// The decision is extracted here so it can be tested without a console or a credential file
/// (the repo pattern — see <c>SpiffeSetModeSettings</c>).
/// </summary>
public static class LoginGate
{
    /// <param name="isAuthenticated">A credential is stored and decrypts.</param>
    /// <param name="isApiKeyMode">
    /// The stored credential is a <c>bax-</c> API key. API keys carry no local expiry the CLI can
    /// check — the server decides — so they are never treated as refreshable or expired here.
    /// </param>
    /// <param name="isOAuthTokenExpired">The stored OAuth session is at or past its expiry.</param>
    /// <param name="force"><c>--force</c> was passed.</param>
    public static LoginAction Decide(
        bool isAuthenticated,
        bool isApiKeyMode,
        bool isOAuthTokenExpired,
        bool force
    )
    {
        if (force || !isAuthenticated)
            return LoginAction.StartLogin;

        if (isApiKeyMode)
            return LoginAction.AlreadyLoggedIn;

        return isOAuthTokenExpired ? LoginAction.TryRefresh : LoginAction.AlreadyLoggedIn;
    }
}
