using System.Web;
using BellaCli.Infrastructure;
using BellaCli.Services;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// #1610 — what a browser login asks Keycloak for. The CLI no longer requests <c>offline_access</c>: an offline token outlives the
/// session limits the server declares for <c>bella-cli</c> (12 h idle, 24 h max), which is what made a copied <c>~/.bella</c> usable
/// for good. The server still OFFERS the scope (older CLIs keep logging in), so this request is the client's half of the rule.
/// </summary>
public class LoginScopeTests
{
    private static string[] RequestedScopes()
    {
        var url = AuthService.BuildAuthorizationUrl(
            new AuthConfig("https://auth.example", "bella-baxter", "bella-cli"), "challenge", "http://localhost:5555/callback");
        var scope = HttpUtility.ParseQueryString(new Uri(url).Query)["scope"];
        return scope!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public void A_browser_login_does_not_request_offline_access()
    {
        Assert.DoesNotContain("offline_access", RequestedScopes());
    }

    [Fact]
    public void A_browser_login_still_requests_the_identity_scopes_it_reads()
    {
        // The CLI reads tenant_* and the profile from the token it gets back.
        Assert.Equal(["openid", "profile", "email"], RequestedScopes());
    }

    [Fact]
    public void The_login_scope_has_one_definition()
    {
        // BuildAuthorizationUrl is the only place a login scope is built; a second literal is how offline_access comes back.
        var source = File.ReadAllText(Path.Combine(CliSourceRoot(), "Services", "AuthService.cs"));
        Assert.DoesNotContain("offline_access\"", source.Replace("`offline_access`", string.Empty), StringComparison.Ordinal);
        Assert.Contains("q[\"scope\"] = LoginScope;", source, StringComparison.Ordinal);
    }

    private static string CliSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BellaBaxter.Cli", "Services", "AuthService.cs")))
            dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("BellaBaxter.Cli/Services/AuthService.cs was not found above the test binaries."), "BellaBaxter.Cli");
    }
}
