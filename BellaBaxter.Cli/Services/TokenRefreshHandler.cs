using System.Net;
using System.Net.Http.Headers;

namespace BellaCli.Services;

/// <summary>
/// A DelegatingHandler that silently refreshes an expired OAuth2 access token
/// before forwarding the request, and retries once on 401 responses.
///
/// Placed as outerHandler in BellaClientProvider so it intercepts after Kiota
/// has already set the Authorization header. When the stored token is about to
/// expire (or has expired), it refreshes via AuthService and replaces the header
/// before the request leaves the process.
/// </summary>
internal sealed class TokenRefreshHandler(AuthService authService) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken ct
    )
    {
        // Pre-flight: if token is within 30s of expiry, refresh it now
        if (authService.IsTokenExpired())
        {
            var freshTokens = await RefreshOrThrowAsync(ct);
            SetBearerHeader(request, freshTokens.AccessToken);
        }

        var response = await base.SendAsync(request, ct);

        // On 401 retry once with a fresh token (handles race where token expired in-flight)
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var freshTokens = await RefreshOrThrowAsync(ct);
            SetBearerHeader(request, freshTokens.AccessToken);
            response.Dispose();
            response = await base.SendAsync(request, ct);
        }

        // Spec 108 (FR-018) — a person with no organisation is refused 403 `organisation-setup-required` on every call that
        // needs one. Say what to do, not "403". Only that type: any other 403 goes on to the command unchanged.
        if (response.StatusCode == HttpStatusCode.Forbidden
            && await OrganisationSetupRefusal.TryReadAsync(response, ct) is { } refusal)
        {
            response.Dispose();
            throw refusal;
        }

        return response;
    }

    private async Task<StoredTokens> RefreshOrThrowAsync(CancellationToken ct)
    {
        try
        {
            return await authService.RefreshAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SessionExpiredException(
                "Session expired. Run 'bella login' to re-authenticate.",
                ex
            );
        }
    }

    private static void SetBearerHeader(HttpRequestMessage request, string accessToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }
}
