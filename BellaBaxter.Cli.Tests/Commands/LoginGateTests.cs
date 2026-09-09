using BellaCli.Commands;

namespace BellaBaxter.Cli.Tests.Commands;

/// <summary>
/// Pilot F7: the login gate consulted only whether a credential file existed, never whether it was
/// still usable. These pin the four states that matter.
/// </summary>
public class LoginGateTests
{
    [Fact]
    public void No_credential_starts_a_login()
    {
        Assert.Equal(
            LoginAction.StartLogin,
            LoginGate.Decide(
                isAuthenticated: false,
                isApiKeyMode: false,
                isOAuthTokenExpired: true,
                force: false
            )
        );
    }

    [Fact]
    public void A_live_oauth_session_is_already_logged_in()
    {
        Assert.Equal(
            LoginAction.AlreadyLoggedIn,
            LoginGate.Decide(
                isAuthenticated: true,
                isApiKeyMode: false,
                isOAuthTokenExpired: false,
                force: false
            )
        );
    }

    [Fact]
    public void An_expired_oauth_session_is_refreshed_not_reported_as_logged_in()
    {
        // This is the pilot's exact state: the file exists, so the old gate said
        // "Already logged in" and exited 0, and every later command failed.
        Assert.Equal(
            LoginAction.TryRefresh,
            LoginGate.Decide(
                isAuthenticated: true,
                isApiKeyMode: false,
                isOAuthTokenExpired: true,
                force: false
            )
        );
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_api_key_is_already_logged_in_whatever_the_oauth_expiry_says(bool expiredFlag)
    {
        // An API key carries no expiry the CLI can check — the server decides — so the OAuth
        // expiry is not evidence about it either way. Never try to refresh one.
        Assert.Equal(
            LoginAction.AlreadyLoggedIn,
            LoginGate.Decide(
                isAuthenticated: true,
                isApiKeyMode: true,
                isOAuthTokenExpired: expiredFlag,
                force: false
            )
        );
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void Force_always_starts_a_login(
        bool isAuthenticated,
        bool isApiKeyMode,
        bool isExpired
    )
    {
        Assert.Equal(
            LoginAction.StartLogin,
            LoginGate.Decide(isAuthenticated, isApiKeyMode, isExpired, force: true)
        );
    }
}
