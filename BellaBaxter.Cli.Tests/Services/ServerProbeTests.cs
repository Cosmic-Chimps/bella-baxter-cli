using BellaCli.Services;
using Microsoft.Kiota.Abstractions;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// Pilot F9: <c>whoami</c>'s only error handling was <c>catch { return null; }</c>, so a revoked
/// credential and an unreachable server produced the same output and the same exit 0. These pin the
/// split, and in particular that we never report a revocation we did not observe.
/// </summary>
public class ServerProbeTests
{
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public void A_server_that_refuses_the_credential_has_judged_it(int status)
    {
        Assert.Equal(ServerProbeFailure.Rejected, ServerProbe.Classify(status));
    }

    [Theory]
    [InlineData(500)] // the server is broken, not our credential
    [InlineData(502)] // a proxy in between
    [InlineData(404)] // wrong deployment / wrong base url
    [InlineData(429)] // rate limited
    [InlineData(null)] // never got a response at all
    public void Anything_else_says_nothing_about_the_credential(int? status)
    {
        // Reporting these as a revocation would send the operator to re-authenticate over an
        // outage — and re-authenticating cannot succeed while the server is down either.
        Assert.Equal(ServerProbeFailure.Unreachable, ServerProbe.Classify(status));
    }

    [Fact]
    public void A_failed_refresh_is_a_rejection()
    {
        // TokenRefreshHandler already tried to renew the session and could not: the credential is
        // dead regardless of what the next call would have returned.
        Assert.Equal(
            ServerProbeFailure.Rejected,
            ServerProbe.Classify(new SessionExpiredException("Session expired."))
        );
    }

    [Fact]
    public void An_api_exception_is_classified_by_its_status()
    {
        Assert.Equal(
            ServerProbeFailure.Rejected,
            ServerProbe.Classify(new ApiException("nope") { ResponseStatusCode = 401 })
        );
        Assert.Equal(
            ServerProbeFailure.Unreachable,
            ServerProbe.Classify(new ApiException("boom") { ResponseStatusCode = 503 })
        );
    }

    [Fact]
    public void A_transport_failure_is_unreachable()
    {
        Assert.Equal(
            ServerProbeFailure.Unreachable,
            ServerProbe.Classify(new HttpRequestException("connection refused"))
        );
        Assert.Equal(
            ServerProbeFailure.Unreachable,
            ServerProbe.Classify(new TimeoutException())
        );
    }

    [Fact]
    public void A_session_expired_exception_still_reads_as_an_InvalidOperationException()
    {
        // Many commands `catch (InvalidOperationException ex)` and print ex.Message. Introducing
        // the dedicated type must not route around them.
        var ex = new SessionExpiredException("Session expired. Run 'bella login' to re-authenticate.");
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
        Assert.Contains("bella login", ex.Message);
    }
}
