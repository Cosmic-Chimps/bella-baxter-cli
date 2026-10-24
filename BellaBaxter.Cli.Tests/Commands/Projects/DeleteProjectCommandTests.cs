using System.Net;
using System.Text;
using System.Text.Json;
using BellaBaxter.Client;
using BellaBaxter.Client.Models;
using BellaCli.Commands.Projects;
using BellaCli.Infrastructure;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;

namespace BellaBaxter.Cli.Tests.Commands.Projects;

/// <summary>
/// Spec 094 US4 — <c>bella projects delete</c>: the summary comes before any delete, the confirmation is never the
/// positional identifier, and every refusal reads by exit code alone. Driven through the REAL generated client over a
/// stub handler; the terminal edges (interactive?, the slug prompt, the summary lines) are injected.
/// </summary>
public class DeleteProjectCommandTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Digest = "digest-abc";

    private const string CustomerProviderPreview = """
        {"projectId":"6f1c2a52-8a0e-4d63-9a43-6f6c1e9f2b11","slug":"payments","name":"Payments",
         "providers":[
           {"providerId":"6f1c2a52-8a0e-4d63-9a43-6f6c1e9f2b12","name":"Acme Vault","type":"Vault","isSystemProvider":false,"providerDeleted":false,"environmentSecrets":4,"projectSecrets":2},
           {"providerId":"6f1c2a52-8a0e-4d63-9a43-6f6c1e9f2b13","name":"Bella System","type":"Vault","isSystemProvider":true,"providerDeleted":false,"environmentSecrets":1,"projectSecrets":0}],
         "totals":{"environments":3,"webhooks":1,"notificationChannels":0,"activeLeases":2,"trustDomains":0,"sshRoles":1,"totpKeys":0,"apiKeysRevoked":5},
         "dataKeyDeleted":true,"previewDigest":"digest-abc"}
        """;

    private const string EmptyPreview = """
        {"projectId":"6f1c2a52-8a0e-4d63-9a43-6f6c1e9f2b11","slug":"payments","name":"Payments","providers":[],
         "totals":{"environments":0},"dataKeyDeleted":false,"previewDigest":"digest-abc"}
        """;

    // ---------------------------------------------------------------- flow

    [Fact]
    public async Task The_summary_is_printed_before_any_delete_with_yes_and_confirm()
    {
        var run = new Run(CustomerProviderPreview);
        var exit = await run.Execute(new DeleteProjectSettings { Identifier = "payments", Yes = true, Confirm = "payments" });

        Assert.Equal(0, exit);
        Assert.Equal(["GET", "summary", "DELETE"], run.Events.Where(e => e != "line").ToArray());
        Assert.True(run.Events.IndexOf("summary") < run.Events.IndexOf("DELETE"));
        Assert.Contains(run.Output.Infos, i => i.Contains("Audit Logs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_yes_and_without_a_terminal_it_refuses_naming_yes_and_confirm_and_sends_no_delete()
    {
        var run = new Run(CustomerProviderPreview);
        var exit = await run.Execute(new DeleteProjectSettings { Identifier = "payments" }, interactive: false);

        Assert.Equal(1, exit);
        var error = Assert.Single(run.Output.Errors);
        Assert.Contains("--yes", error, StringComparison.Ordinal);
        Assert.Contains("--confirm", error, StringComparison.Ordinal);
        Assert.DoesNotContain("DELETE", run.Events);
        Assert.Contains("summary", run.Events); // the summary is still shown
    }

    [Fact]
    public async Task Yes_without_confirm_refuses_naming_confirm_and_sends_no_delete()
    {
        var run = new Run(CustomerProviderPreview);
        var exit = await run.Execute(new DeleteProjectSettings { Identifier = "payments", Yes = true });

        Assert.Equal(1, exit);
        Assert.Contains("--confirm", Assert.Single(run.Output.Errors), StringComparison.Ordinal);
        Assert.DoesNotContain("DELETE", run.Events);
    }

    [Fact]
    public async Task Dry_run_sends_no_delete_and_exits_zero()
    {
        var run = new Run(CustomerProviderPreview);
        var exit = await run.Execute(new DeleteProjectSettings { Identifier = "payments", DryRun = true, Yes = true, Confirm = "payments" });

        Assert.Equal(0, exit);
        Assert.DoesNotContain("DELETE", run.Events);
        Assert.Contains("summary", run.Events);
    }

    [Fact]
    public async Task Force_is_the_same_flag_as_yes()
    {
        // One property carries "-y|--yes|-f|--force"; setting it is what either spelling does.
        var flags = typeof(DeleteProjectSettings).GetProperty(nameof(DeleteProjectSettings.Yes))!
            .GetCustomAttributes(typeof(Spectre.Console.Cli.CommandOptionAttribute), false)
            .Cast<Spectre.Console.Cli.CommandOptionAttribute>().Single();
        Assert.Equal(["y", "f"], flags.ShortNames.OrderByDescending(s => s));
        Assert.Equal(["yes", "force"], flags.LongNames);

        var run = new Run(CustomerProviderPreview);
        var exit = await run.Execute(new DeleteProjectSettings { Identifier = "payments", Yes = true, Confirm = "payments" });
        Assert.Equal(0, exit);
        Assert.Contains("DELETE", run.Events);
    }

    [Fact]
    public async Task The_delete_carries_the_confirm_value_and_the_digest_never_the_positional_identifier()
    {
        var run = new Run(CustomerProviderPreview);
        // The identifier is an id here; the confirmation is the slug the operator typed with --confirm.
        var identifier = "6f1c2a52-8a0e-4d63-9a43-6f6c1e9f2b11";
        var exit = await run.Execute(new DeleteProjectSettings { Identifier = identifier, Yes = true, Confirm = "payments" });

        Assert.Equal(0, exit);
        var delete = run.Requests.Single(r => r.Method == HttpMethod.Delete);
        var query = System.Web.HttpUtility.ParseQueryString(delete.RequestUri!.Query);
        Assert.Equal("payments", query["confirmProjectSlug"]);
        Assert.Equal(Digest, query["previewDigest"]);
        Assert.NotEqual(identifier, query["confirmProjectSlug"]);
    }

    [Fact]
    public async Task A_wrong_confirm_is_sent_as_given_and_the_422_exits_refused()
    {
        var run = new Run(CustomerProviderPreview, deleteStatus: HttpStatusCode.UnprocessableEntity,
            deleteBody: Problem("project-deletion-confirmation-mismatch", 422));
        var exit = await run.Execute(new DeleteProjectSettings { Identifier = "payments", Yes = true, Confirm = "Payments" });

        Assert.Equal(DeleteProjectCommand.Refused, exit);
        var query = System.Web.HttpUtility.ParseQueryString(run.Requests.Single(r => r.Method == HttpMethod.Delete).RequestUri!.Query);
        Assert.Equal("Payments", query["confirmProjectSlug"]);
    }

    [Fact]
    public async Task Interactive_with_the_exact_slug_deletes_and_sends_the_answer()
    {
        var run = new Run(CustomerProviderPreview) { Answer = "payments" };
        var exit = await run.Execute(new DeleteProjectSettings { Identifier = "6f1c2a52-8a0e-4d63-9a43-6f6c1e9f2b11" }, interactive: true);

        Assert.Equal(0, exit);
        var query = System.Web.HttpUtility.ParseQueryString(run.Requests.Single(r => r.Method == HttpMethod.Delete).RequestUri!.Query);
        Assert.Equal("payments", query["confirmProjectSlug"]);
    }

    [Theory]
    [InlineData("Payments")]
    [InlineData(" payments")]
    [InlineData("payments ")]
    [InlineData("")]
    public async Task Interactive_with_anything_but_the_exact_slug_cancels_and_exits_zero(string answer)
    {
        var run = new Run(CustomerProviderPreview) { Answer = answer };
        var exit = await run.Execute(new DeleteProjectSettings { Identifier = "payments" }, interactive: true);

        Assert.Equal(0, exit);
        Assert.DoesNotContain("DELETE", run.Events);
        Assert.Contains("Cancelled.", run.Output.Infos);
    }

    // ---------------------------------------------------------------- json

    [Fact]
    public async Task Json_writes_exactly_one_document_with_preview_and_result_and_no_summary_lines()
    {
        var run = new Run(CustomerProviderPreview);
        var exit = await run.Execute(new DeleteProjectSettings { Identifier = "payments", Yes = true, Confirm = "payments", Json = true });

        Assert.Equal(0, exit);
        var doc = JsonSerializer.SerializeToElement(Assert.Single(run.Output.Objects));
        Assert.Equal("payments", doc.GetProperty("preview").GetProperty("slug").GetString());
        Assert.Equal(Digest, doc.GetProperty("preview").GetProperty("previewDigest").GetString());
        Assert.True(doc.GetProperty("result").GetProperty("success").GetBoolean());
        Assert.DoesNotContain("summary", run.Events);
        Assert.Empty(run.Output.Successes);
    }

    [Fact]
    public async Task Json_dry_run_has_only_the_preview()
    {
        var run = new Run(CustomerProviderPreview);
        var exit = await run.Execute(new DeleteProjectSettings { Identifier = "payments", DryRun = true, Json = true });

        Assert.Equal(0, exit);
        var doc = JsonSerializer.SerializeToElement(Assert.Single(run.Output.Objects));
        Assert.True(doc.TryGetProperty("preview", out _));
        Assert.False(doc.TryGetProperty("result", out _));
        Assert.DoesNotContain("DELETE", run.Events);
    }

    // ---------------------------------------------------------------- classification

    [Theory]
    [InlineData(422, "project-deletion-confirmation-missing", 2)]
    [InlineData(422, "project-deletion-confirmation-mismatch", 2)]
    [InlineData(409, "project-deletion-preview-stale", 2)]
    [InlineData(409, null, 2)]
    [InlineData(503, "project-deletion-preview-unavailable", 3)]
    [InlineData(503, null, 3)]
    [InlineData(404, null, 1)]
    [InlineData(500, null, 1)]
    public async Task Refusals_map_to_exit_codes(int status, string? code, int expected)
    {
        var body = code is null ? "{}" : Problem(code, status);
        var run = new Run(CustomerProviderPreview, deleteStatus: (HttpStatusCode)status, deleteBody: body);
        var exit = await run.Execute(new DeleteProjectSettings { Identifier = "payments", Yes = true, Confirm = "payments" });

        Assert.Equal(expected, exit);
        Assert.Single(run.Output.Errors);
    }

    [Fact]
    public async Task A_refused_preview_503_exits_transient_and_deletes_nothing()
    {
        var run = new Run(Problem("project-deletion-preview-unavailable", 503), previewStatus: HttpStatusCode.ServiceUnavailable);
        var exit = await run.Execute(new DeleteProjectSettings { Identifier = "payments", Yes = true, Confirm = "payments" });

        Assert.Equal(DeleteProjectCommand.Transient, exit);
        Assert.DoesNotContain("DELETE", run.Events);
    }

    [Fact]
    public async Task A_404_says_not_found_or_not_allowed_without_guessing_which()
    {
        var run = new Run("{}", previewStatus: HttpStatusCode.NotFound);
        var exit = await run.Execute(new DeleteProjectSettings { Identifier = "payments", Yes = true, Confirm = "payments" });

        Assert.Equal(DeleteProjectCommand.Failed, exit);
        Assert.Contains("not found or not allowed", Assert.Single(run.Output.Errors), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Exit_codes_are_distinct()
    {
        int[] codes = [DeleteProjectCommand.Deleted, DeleteProjectCommand.Failed, DeleteProjectCommand.Refused, DeleteProjectCommand.Transient];
        Assert.Equal(codes.Length, codes.Distinct().Count());
        Assert.Equal(0, DeleteProjectCommand.Deleted);
    }

    // ---------------------------------------------------------------- decision and summary

    [Fact]
    public void Only_the_exact_slug_confirms()
    {
        Assert.True(DeleteProjectCommand.IsConfirmed("payments", "payments"));
        Assert.False(DeleteProjectCommand.IsConfirmed("Payments", "payments"));
        Assert.False(DeleteProjectCommand.IsConfirmed(" payments", "payments"));
        Assert.False(DeleteProjectCommand.IsConfirmed("payments\n", "payments"));
        Assert.False(DeleteProjectCommand.IsConfirmed("", "payments"));
        Assert.False(DeleteProjectCommand.IsConfirmed(null, "payments"));
    }

    [Fact]
    public async Task The_summary_names_the_customers_own_account_but_not_the_system_provider_and_says_values_are_lost()
    {
        var preview = await Preview(CustomerProviderPreview);
        var lines = DeleteProjectCommand.RenderSummary(preview);

        var customer = Assert.Single(lines, l => l.Contains("Acme Vault", StringComparison.Ordinal));
        var system = Assert.Single(lines, l => l.Contains("Bella System", StringComparison.Ordinal));
        Assert.Contains("in your own account", customer, StringComparison.Ordinal);
        Assert.DoesNotContain("in your own account", system, StringComparison.Ordinal);
        Assert.Contains(lines, l => l.Contains("cannot be restored from Bella", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("3 environment(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_empty_provider_list_says_no_secrets_will_be_deleted_from_any_provider()
    {
        var lines = DeleteProjectCommand.RenderSummary(await Preview(EmptyPreview));

        Assert.Contains(lines, l => l.Contains("No secrets will be deleted from any provider", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("in your own account", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- harness

    private static string Problem(string code, int status) =>
        $$"""{"type":"https://bellabaxter.dev/problems/{{code}}","title":"{{code}}","status":{{status}},"detail":"d"}""";

    private static async Task<ProjectDeletionPreview> Preview(string body)
    {
        var client = Client(_ => Json(HttpStatusCode.OK, body));
        return (await client.Api.V1.Projects["payments"].DeletionPreview.GetAsync(cancellationToken: Ct))!;
    }

    private sealed class Run
    {
        private readonly HttpStatusCode _previewStatus;
        private readonly string _previewBody;
        private readonly HttpStatusCode _deleteStatus;
        private readonly string _deleteBody;

        public Run(
            string previewBody, HttpStatusCode previewStatus = HttpStatusCode.OK,
            HttpStatusCode deleteStatus = HttpStatusCode.OK, string deleteBody = """{"success":true,"message":"ok"}""")
        {
            _previewBody = previewBody;
            _previewStatus = previewStatus;
            _deleteStatus = deleteStatus;
            _deleteBody = deleteBody;
        }

        public List<string> Events { get; } = [];
        public List<HttpRequestMessage> Requests { get; } = [];
        public RecordingOutput Output { get; } = new();
        public string? Answer { get; set; }

        public Task<int> Execute(DeleteProjectSettings settings, bool interactive = false)
        {
            var client = Client(req =>
            {
                Requests.Add(req);
                if (req.Method == HttpMethod.Delete)
                {
                    Events.Add("DELETE");
                    return Json(_deleteStatus, _deleteBody, _deleteStatus == HttpStatusCode.OK ? "application/json" : "application/problem+json");
                }
                Events.Add("GET");
                return Json(_previewStatus, _previewBody, _previewStatus == HttpStatusCode.OK ? "application/json" : "application/problem+json");
            });
            var command = new DeleteProjectCommand(null!, Output);
            var summaryStarted = false;
            return command.RunAsync(
                client, settings, interactive,
                _ =>
                {
                    if (!summaryStarted) { summaryStarted = true; Events.Add("summary"); }
                },
                _ => Answer,
                Ct);
        }
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

    private sealed class RecordingOutput : IOutputWriter
    {
        public List<string> Errors { get; } = [];
        public List<string> Infos { get; } = [];
        public List<string> Successes { get; } = [];
        public List<object> Objects { get; } = [];
        public Task StatusAsync(string message, Func<Task> work) => work();
        public Task StatusAsync(string message, Func<Action<string>, Task> work) => work(_ => { });
        public void WriteObject<T>(T obj) => Objects.Add(obj!);
        public void WriteList<T>(IEnumerable<T> items) { }
        public void WriteTable(string[] headers, IEnumerable<string[]> rows) { }
        public void WriteSuccess(string message) => Successes.Add(message);
        public void WriteError(string message, string? code = null) => Errors.Add(message);
        public void WriteWarning(string message) { }
        public void WriteInfo(string message) => Infos.Add(message);
    }
}
