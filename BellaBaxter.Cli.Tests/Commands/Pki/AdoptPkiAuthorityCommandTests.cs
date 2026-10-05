using System.Net;
using System.Text;
using BellaBaxter.Client;
using BellaBaxter.Client.Models;
using BellaCli.Commands.Pki;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;

namespace BellaBaxter.Cli.Tests.Commands.Pki;

/// <summary>
/// #1147 — <c>bella pki adopt</c>: what it calls, what it says before it acts, and how a refusal reads by exit
/// code alone.
/// </summary>
/// <remarks>
/// <para>The 503 <c>acme-not-enabled</c> refusal (#1118) is the case this command most needs to get right: nothing
/// was recorded and the right move is to retry. Reading it as a generic failure tells the operator to investigate a
/// transient; reading it as success leaves ACME clients on an authority that cannot answer them. So the refusals
/// are driven through the REAL generated client over a stub handler — the classification is only worth something
/// if it receives what Kiota actually throws.</para>
/// </remarks>
public class AdoptPkiAuthorityCommandTests
{
    [Fact]
    public async Task Adoption_posts_to_the_environment_adoption_route_and_returns_the_mount()
    {
        HttpRequestMessage? seen = null;
        var client = Client(req =>
        {
            seen = req;
            return Json(HttpStatusCode.OK, """{"status":"own","mountPath":"pki-acme-prod","notice":"n"}""");
        });

        var result = await client.Api.V1.Projects["acme"].Environments["prod"].Pki.Authority.Adoption.PostAsync();

        Assert.Equal(HttpMethod.Post, seen!.Method);
        Assert.EndsWith("/api/v1/projects/acme/environments/prod/pki/authority/adoption", seen.RequestUri!.AbsolutePath);
        Assert.Equal("own", result!.Status);
        Assert.Equal("pki-acme-prod", result.MountPath);
    }

    [Fact]
    public async Task A_503_acme_not_enabled_is_TRANSIENT_never_a_plain_failure()
    {
        var ex = await Refusal(HttpStatusCode.ServiceUnavailable, "acme-not-enabled");

        var (code, message) = AdoptPkiAuthorityCommand.Classify(ex);

        Assert.Equal(AdoptPkiAuthorityCommand.Transient, code);
        // Today's client cannot tell acme-not-enabled from authority-unreadable (the slice's 503 has no schema, so
        // Kiota drops the body); both are transient and both mean nothing was recorded, which is what matters.
        Assert.Contains("nothing was recorded", message, StringComparison.Ordinal);
        Assert.Contains("Retry", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_503_authority_unreadable_is_transient_too()
    {
        var (code, _) = AdoptPkiAuthorityCommand.Classify(await Refusal(HttpStatusCode.ServiceUnavailable, "authority-unreadable"));
        Assert.Equal(AdoptPkiAuthorityCommand.Transient, code);
    }

    [Fact]
    public async Task A_409_authority_not_prepared_says_to_configure_first()
    {
        var (code, message) = AdoptPkiAuthorityCommand.Classify(await Refusal(HttpStatusCode.Conflict, "authority-not-prepared"));

        Assert.Equal(AdoptPkiAuthorityCommand.NotPrepared, code);
        Assert.Contains("bella pki configure", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_404_is_a_failure_that_does_not_guess_which_of_missing_or_forbidden()
    {
        var (code, message) = AdoptPkiAuthorityCommand.Classify(await Refusal(HttpStatusCode.NotFound, null));

        Assert.Equal(AdoptPkiAuthorityCommand.Failed, code);
        Assert.Contains("not allowed", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_typed_problem_is_read_by_its_type_when_a_newer_client_surfaces_one()
    {
        // Today's client has no error mapping for this route (the slice declares untyped 409/503), so Kiota throws a
        // bare ApiException. If the slice gains typed problems, ProblemDetails arrives instead — still classified.
        var problem = new ProblemDetails { Type = "acme-not-enabled", ResponseStatusCode = 503 };
        Assert.Equal(AdoptPkiAuthorityCommand.Transient, AdoptPkiAuthorityCommand.Classify(problem).ExitCode);
    }

    [Fact]
    public void The_confirmation_states_the_consequence_before_it_is_made()
    {
        Assert.Contains("reject", AdoptPkiAuthorityCommand.Consequence, StringComparison.Ordinal);
        Assert.Contains("tenant-wide authority stays in place", AdoptPkiAuthorityCommand.Consequence, StringComparison.Ordinal);
        Assert.Contains("cannot be undone", AdoptPkiAuthorityCommand.Consequence, StringComparison.Ordinal);
    }

    [Fact]
    public void Exit_codes_are_distinct()
    {
        int[] codes =
        [
            AdoptPkiAuthorityCommand.Adopted, AdoptPkiAuthorityCommand.Failed,
            AdoptPkiAuthorityCommand.NotPrepared, AdoptPkiAuthorityCommand.Transient,
        ];
        Assert.Equal(codes.Length, codes.Distinct().Count());
        Assert.Equal(0, AdoptPkiAuthorityCommand.Adopted);
    }

    private static async Task<ApiException> Refusal(HttpStatusCode status, string? type)
    {
        var body = type is null ? "{}" : $$"""{"type":"{{type}}","title":"{{type}}","status":{{(int)status}},"detail":"d"}""";
        var client = Client(_ => Json(status, body, "application/problem+json"));
        return await Assert.ThrowsAnyAsync<ApiException>(() =>
            client.Api.V1.Projects["acme"].Environments["prod"].Pki.Authority.Adoption.PostAsync());
    }

    private static BellaClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var http = new HttpClient(new Stub(respond));
        var adapter = new HttpClientRequestAdapter(new AnonymousAuthenticationProvider(), httpClient: http)
        {
            BaseUrl = "https://bella.test",
        };
        return new BellaClient(adapter);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body, string mediaType = "application/json") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
