using System.Net;
using System.Text;
using System.Text.Json;
using BellaBaxter.Client;
using BellaCli.Commands.Auth;
using BellaCli.Commands.Orgs;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;

namespace BellaBaxter.Cli.Tests.Commands.Orgs;

/// <summary>
/// #1141 — the API now refuses an API key on <c>GET /api/tenants/my-tenants</c> (and switch, devices,
/// profile) with 403 <c>person-only</c>, where it used to crash with a 500. <c>bella org list</c> and
/// <c>bella org switch</c> must turn that into an instruction, through the real generated client.
/// </summary>
public class PersonOnlyRefusalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string PersonOnlyBody =
        """{"type":"person-only","title":"This endpoint acts for a signed-in person, and an API key is not one.","status":403,"detail":"Tenant membership belongs to a signed-in person."}""";

    [Fact]
    public async Task Org_list_with_an_api_key_says_to_log_in_interactively()
    {
        var output = new CapturingOutput();
        var client = ClientAnswering(HttpStatusCode.Forbidden, PersonOnlyBody, out var requests);

        var orgs = await OrgQueries.FetchOrgsAsync(client, callerIsApiKey: true, output, Ct);

        Assert.Null(orgs);
        Assert.Contains("/api/tenants/my-tenants", Assert.Single(requests));
        var (message, code) = Assert.Single(output.Errors);
        Assert.Equal(PersonOnlyRefusal.Message, message);
        Assert.Contains("bella login", message);
        Assert.Equal("person-only", code);
    }

    [Fact]
    public async Task A_person_who_gets_a_403_is_not_told_to_stop_using_an_api_key()
    {
        // The generated client surfaces this 403 without a body, so it is the refusal only when the caller
        // is a key; for a person it propagates as it always did.
        var output = new CapturingOutput();
        var client = ClientAnswering(HttpStatusCode.Forbidden, PersonOnlyBody, out _);

        await Assert.ThrowsAnyAsync<ApiException>(() => OrgQueries.FetchOrgsAsync(client, callerIsApiKey: false, output, Ct));
        Assert.Empty(output.Errors);
    }

    [Fact]
    public async Task A_person_still_gets_their_organizations()
    {
        var output = new CapturingOutput();
        var client = ClientAnswering(
            HttpStatusCode.OK,
            """[{"tenantId":"6f1c2a52-8a0e-4d63-9a43-6f6c1e9f2b11","tenantName":"Acme","slug":"acme","role":"Owner"}]""",
            out _);

        var orgs = await OrgQueries.FetchOrgsAsync(client, callerIsApiKey: false, output, Ct);

        Assert.Equal("acme", Assert.Single(orgs!).Slug);
        Assert.Empty(output.Errors);
    }

    [Fact]
    public void The_typed_problem_is_recognised_by_its_code_alone()
    {
        // Once the SDK is regenerated with the declared 403, Kiota hands back ProblemDetails; the code
        // decides, whoever the caller is.
        var problem = new BellaBaxter.Client.Models.ProblemDetails { Type = "person-only", ResponseStatusCode = 403 };
        Assert.True(PersonOnlyRefusal.Is(problem, callerIsApiKey: false));

        var other = new BellaBaxter.Client.Models.ProblemDetails { Type = "audit-streaming-plan-required", ResponseStatusCode = 403 };
        Assert.False(PersonOnlyRefusal.Is(other, callerIsApiKey: true));

        Assert.False(PersonOnlyRefusal.Is(new ApiException { ResponseStatusCode = 404 }, callerIsApiKey: true));
        Assert.False(PersonOnlyRefusal.Is(new InvalidOperationException(), callerIsApiKey: true));
    }

    [Fact]
    public void Auth_setup_explains_the_refusal_instead_of_a_bare_status()
    {
        var message = AuthSetupCommand.Describe(new ApiException { ResponseStatusCode = 403 }, callerIsApiKey: true);

        Assert.Contains("bella login", message);
        Assert.Contains("when the API key is created", message);
    }

    // ── plumbing ──────────────────────────────────────────────────────────────

    private static BellaClient ClientAnswering(HttpStatusCode status, string body, out List<string> requests)
    {
        var seen = new List<string>();
        requests = seen;
        var http = new HttpClient(new StubHandler(request =>
        {
            seen.Add(request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8,
                    status == HttpStatusCode.OK ? "application/json" : "application/problem+json"),
            };
        }));
        var adapter = new HttpClientRequestAdapter(new AnonymousAuthenticationProvider(), httpClient: http)
        {
            BaseUrl = "https://api.example.com",
        };
        return new BellaClient(adapter);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private sealed class CapturingOutput : IOutputWriter
    {
        public List<(string Message, string? Code)> Errors { get; } = new();
        public Task StatusAsync(string message, Func<Task> work) => work();
        public Task StatusAsync(string message, Func<Action<string>, Task> work) => work(_ => { });
        public void WriteObject<T>(T obj) { }
        public void WriteList<T>(IEnumerable<T> items) { }
        public void WriteTable(string[] headers, IEnumerable<string[]> rows) { }
        public void WriteSuccess(string message) { }
        public void WriteError(string message, string? code = null) => Errors.Add((message, code));
        public void WriteWarning(string message) { }
        public void WriteInfo(string message) { }
    }
}
