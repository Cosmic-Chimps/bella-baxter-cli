using System.Net;
using System.Text;
using System.Text.Json;
using BellaBaxter.Client;
using BellaCli.Commands.Auth.Devices;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;

namespace BellaBaxter.Cli.Tests;

/// <summary>
/// Spec 099 US3 (T023) — <c>bella auth devices revoke</c>: the preview, <c>--rotate</c>, the prompts, <c>-o json</c>, the exit
/// codes and the fingerprint lookup, through the real generated client over a stub handler (as
/// <c>PersonOnlyRefusalTests</c> does), so the requests that go out are the ones the flow really makes.
/// </summary>
public class AuthDevicesRevokeCommandTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Fingerprint = "SHA256:abcDEF123";
    private static readonly Guid DeviceId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid RunId = Guid.Parse("99999999-8888-7777-6666-555555555555");
    private static readonly string DevicePath = $"/api/v1/tenants/me/devices/{DeviceId}";

    private static string Exposure(bool usedOrganisationKey = false, int owed = 1, string state = "active") =>
        $$"""
        {"deviceId":"{{DeviceId}}","fingerprint":"{{Fingerprint}}","label":"laptop","state":"{{state}}",
         "usedOrganisationKey":{{usedOrganisationKey.ToString().ToLowerInvariant()}},"projectCount":1,"owedCount":{{owed}},"widenedProjectCount":0,
         "projects":[{"projectId":"{{Guid.NewGuid()}}","projectName":"Alpha","environments":[{"environmentId":"{{Guid.NewGuid()}}","name":"dev"}],
           "usesOrganisationKey":{{usedOrganisationKey.ToString().ToLowerInvariant()}},"coveringReadAt":"2026-11-01T00:00:00Z","owed":{{(owed > 0).ToString().ToLowerInvariant()}},"callerMayRotate":true}]}
        """;

    private static readonly string Run =
        $$"""
        {"id":"{{RunId}}","deviceId":"{{DeviceId}}","state":"queued","startedBy":"{{Guid.NewGuid()}}","startedAt":"2026-11-05T00:00:00Z",
         "resealed":0,"alreadyCurrent":0,"notResealed":0,"projects":[],"notMigrated":[]}
        """;

    private static readonly string DeviceList =
        $$"""[{"id":"{{DeviceId}}","fingerprint":"{{Fingerprint}}","label":"laptop","state":"active"}]""";

    private static string Problem(string type, int status) =>
        $$"""{"type":"{{type}}","title":"{{type}}","status":{{status}},"detail":"detail of {{type}}"}""";

    /// <summary>A server: each rule answers the requests whose "METHOD path?query" it matches; anything else is a failing test.</summary>
    private sealed class Server
    {
        private readonly List<(string Prefix, Func<HttpResponseMessage> Answer)> _rules = [];

        public List<string> Requests { get; } = [];

        public Server On(string methodAndPath, HttpStatusCode status, string? body = null)
        {
            _rules.Add((methodAndPath, () =>
            {
                var response = new HttpResponseMessage(status);
                if (body is not null)
                    response.Content = new StringContent(
                        body, Encoding.UTF8, (int)status >= 400 ? "application/problem+json" : "application/json");
                return response;
            }));
            return this;
        }

        public bool Saw(string prefix) => Requests.Any(r => r.StartsWith(prefix, StringComparison.Ordinal));

        public int IndexOf(string prefix) => Requests.FindIndex(r => r.StartsWith(prefix, StringComparison.Ordinal));

        public BellaClient Client()
        {
            var http = new HttpClient(new StubHandler(request =>
            {
                var key = $"{request.Method} {request.RequestUri!.PathAndQuery}";
                Requests.Add(key);
                var rule = _rules.FirstOrDefault(r => key.StartsWith(r.Prefix, StringComparison.Ordinal));
                return rule.Answer is null
                    ? throw new InvalidOperationException($"unexpected request {key}")
                    : rule.Answer();
            }));
            var adapter = new HttpClientRequestAdapter(new AnonymousAuthenticationProvider(), httpClient: http)
            {
                BaseUrl = "https://api.example.com",
            };
            return new BellaClient(adapter);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class CapturingOutput : IOutputWriter
    {
        public List<string> Infos { get; } = [];
        public List<string> Warnings { get; } = [];
        public List<string> Successes { get; } = [];
        public List<(string Message, string? Code)> Errors { get; } = [];
        public List<string> Documents { get; } = [];

        public Task StatusAsync(string message, Func<Task> work) => work();
        public Task StatusAsync(string message, Func<Action<string>, Task> work) => work(_ => { });
        public void WriteObject<T>(T obj) => Documents.Add(JsonSerializer.Serialize(obj));
        public void WriteList<T>(IEnumerable<T> items) { }
        public void WriteTable(string[] headers, IEnumerable<string[]> rows) { }
        public void WriteSuccess(string message) => Successes.Add(message);
        public void WriteError(string message, string? code = null) => Errors.Add((message, code));
        public void WriteWarning(string message) => Warnings.Add(message);
        public void WriteInfo(string message) => Infos.Add(message);

        public bool SaidAnything => Infos.Count + Warnings.Count + Successes.Count + Errors.Count > 0;
    }

    private static Task<int> RunAsync(
        Server server,
        CapturingOutput output,
        string device = "DEVICE",
        bool rotate = false,
        bool yes = false,
        bool json = false,
        bool interactive = false,
        bool apiKey = false,
        Func<string, bool>? confirm = null) =>
        DeviceRevocation.RunAsync(
            server.Client(),
            new DeviceRevocation.Options(
                device == "DEVICE" ? DeviceId.ToString() : device, rotate, yes, json, interactive, ThisMachineFingerprint: null, apiKey),
            output,
            confirm ?? (_ => throw new InvalidOperationException("no question was expected")),
            Ct);

    private static Server HealthyServer(string? exposure = null) =>
        new Server()
            .On($"GET {DevicePath}/key-exposure", HttpStatusCode.OK, exposure ?? Exposure())
            .On($"DELETE {DevicePath}", HttpStatusCode.NoContent)
            .On($"POST {DevicePath}/key-rotation", HttpStatusCode.Accepted, Run);

    // ── --rotate and --yes ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rotate_starts_the_rotation_after_the_revoke()
    {
        var server = HealthyServer();
        var output = new CapturingOutput();

        var exit = await RunAsync(server, output, rotate: true, yes: true);

        Assert.Equal(DeviceRevocation.ExitOk, exit);
        Assert.True(server.Saw($"POST {DevicePath}/key-rotation"));
        Assert.True(server.IndexOf($"DELETE {DevicePath}") < server.IndexOf($"POST {DevicePath}/key-rotation"), "the revoke comes first");
        Assert.Contains(output.Infos, i => i.Contains(RunId.ToString(), StringComparison.Ordinal) && i.Contains("background", StringComparison.Ordinal));
        Assert.Empty(output.Errors);
    }

    [Fact]
    public async Task Yes_alone_revokes_and_never_starts_a_rotation()
    {
        var server = HealthyServer();

        var exit = await RunAsync(server, new CapturingOutput(), yes: true);

        Assert.Equal(DeviceRevocation.ExitOk, exit);
        Assert.True(server.Saw($"DELETE {DevicePath}"));
        Assert.False(server.Saw("POST"), "--yes must not imply --rotate");
    }

    [Fact]
    public async Task A_device_revoked_earlier_can_still_be_rotated_because_the_revoke_answers_204()
    {
        var server = HealthyServer(Exposure(state: "revoked"));

        var exit = await RunAsync(server, new CapturingOutput(), rotate: true, yes: true);

        Assert.Equal(DeviceRevocation.ExitOk, exit);
        Assert.True(server.Saw($"POST {DevicePath}/key-rotation"));
    }

    [Fact]
    public async Task Rotate_with_nothing_owed_says_so_and_does_not_call_the_server()
    {
        var server = HealthyServer(Exposure(owed: 0));
        var output = new CapturingOutput();

        var exit = await RunAsync(server, output, rotate: true, yes: true);

        Assert.Equal(DeviceRevocation.ExitOk, exit);
        Assert.False(server.Saw("POST"));
        Assert.Contains(output.Infos, i => i.Contains("nothing to rotate", StringComparison.OrdinalIgnoreCase));
    }

    // ── the prompts ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_rotation_question_is_asked_after_the_revoke_confirmation_and_defaults_to_no_in_the_caller()
    {
        var asked = new List<string>();
        var server = HealthyServer();

        var exit = await RunAsync(server, new CapturingOutput(), interactive: true, confirm: q => { asked.Add(q); return !q.StartsWith("Also", StringComparison.Ordinal); });

        Assert.Equal(DeviceRevocation.ExitOk, exit);
        Assert.Equal(2, asked.Count);
        Assert.StartsWith("Revoke device", asked[0], StringComparison.Ordinal);
        Assert.Equal("Also rotate the data keys of 1 project(s)?", asked[1]);
        Assert.True(server.Saw($"DELETE {DevicePath}"));
        Assert.False(server.Saw("POST"), "a no to the second question must not rotate");
    }

    [Fact]
    public async Task Answering_yes_to_the_rotation_question_rotates()
    {
        var server = HealthyServer();

        await RunAsync(server, new CapturingOutput(), interactive: true, confirm: _ => true);

        Assert.True(server.Saw($"POST {DevicePath}/key-rotation"));
    }

    [Fact]
    public async Task Declining_the_revoke_revokes_nothing_and_exits_zero()
    {
        var server = HealthyServer();
        var output = new CapturingOutput();

        var exit = await RunAsync(server, output, interactive: true, confirm: _ => false);

        Assert.Equal(DeviceRevocation.ExitOk, exit);
        Assert.False(server.Saw("DELETE"));
        Assert.Contains("Nothing was revoked.", output.Infos);
    }

    [Fact]
    public async Task Rotate_given_as_a_flag_is_not_asked_again()
    {
        var asked = new List<string>();

        await RunAsync(HealthyServer(), new CapturingOutput(), rotate: true, interactive: true, confirm: q => { asked.Add(q); return true; });

        Assert.Equal(["Revoke device laptop?"], asked);
    }

    [Fact]
    public async Task Without_a_terminal_nothing_is_ever_asked()
    {
        // The confirm delegate throws if called.
        var exit = await RunAsync(HealthyServer(), new CapturingOutput(), interactive: false);

        Assert.Equal(DeviceRevocation.ExitOk, exit);
    }

    // ── the fingerprint lookup ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_members_fingerprint_revoke_never_needs_the_tenant_listing()
    {
        var server = HealthyServer()
            .On("GET /api/v1/tenants/me/devices?scope=mine", HttpStatusCode.OK, DeviceList)
            .On("GET /api/v1/tenants/me/devices?scope=tenant", HttpStatusCode.Forbidden, Problem("forbidden", 403));

        var exit = await RunAsync(server, new CapturingOutput(), device: Fingerprint, yes: true);

        Assert.Equal(DeviceRevocation.ExitOk, exit);
        Assert.False(server.Saw("GET /api/v1/tenants/me/devices?scope=tenant"));
        Assert.True(server.Saw($"DELETE {DevicePath}"));
    }

    [Fact]
    public async Task A_403_on_the_tenant_listing_reads_as_no_match_not_a_failure()
    {
        var server = new Server()
            .On("GET /api/v1/tenants/me/devices?scope=mine", HttpStatusCode.OK, "[]")
            .On("GET /api/v1/tenants/me/devices?scope=tenant", HttpStatusCode.Forbidden, Problem("forbidden", 403));
        var output = new CapturingOutput();

        var exit = await RunAsync(server, output, device: Fingerprint, yes: true);

        Assert.Equal(DeviceRevocation.ExitFailure, exit);
        var (message, code) = Assert.Single(output.Errors);
        Assert.Equal($"No device matches '{Fingerprint}'.", message);
        Assert.Equal("not-found", code);
        Assert.False(server.Saw("DELETE"));
    }

    [Fact]
    public async Task An_admin_finds_another_members_device_through_the_tenant_listing()
    {
        var server = HealthyServer()
            .On("GET /api/v1/tenants/me/devices?scope=mine", HttpStatusCode.OK, "[]")
            .On("GET /api/v1/tenants/me/devices?scope=tenant", HttpStatusCode.OK, DeviceList);

        var exit = await RunAsync(server, new CapturingOutput(), device: Fingerprint, yes: true);

        Assert.Equal(DeviceRevocation.ExitOk, exit);
        Assert.True(server.IndexOf("GET /api/v1/tenants/me/devices?scope=mine") < server.IndexOf("GET /api/v1/tenants/me/devices?scope=tenant"));
        Assert.True(server.Saw($"DELETE {DevicePath}"));
    }

    [Fact]
    public async Task An_API_key_is_told_the_command_needs_a_login_and_nothing_is_revoked()
    {
        var server = new Server()
            .On("GET /api/v1/tenants/me/devices?scope=mine", HttpStatusCode.Forbidden, Problem("person-only", 403));
        var output = new CapturingOutput();

        var exit = await RunAsync(server, output, device: Fingerprint, yes: true, apiKey: true);

        Assert.Equal(DeviceRevocation.ExitFailure, exit);
        var (message, code) = Assert.Single(output.Errors);
        Assert.Equal(PersonOnlyRefusal.Message, message);
        Assert.Equal(PersonOnlyRefusal.Code, code);
        Assert.False(server.Saw("DELETE"));
    }

    // ── exit code 2 ──────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("rotation-not-permitted", 403, "Owners and Admins")]
    [InlineData("rotation-in-progress", 409, "already running")]
    [InlineData("key-store-unreadable", 503, "key-store-unreadable")]
    public async Task A_rotation_refused_after_the_revoke_exits_2_and_the_device_stays_revoked(string type, int status, string saying)
    {
        var server = new Server()
            .On($"GET {DevicePath}/key-exposure", HttpStatusCode.OK, Exposure())
            .On($"DELETE {DevicePath}", HttpStatusCode.NoContent)
            .On($"POST {DevicePath}/key-rotation", (HttpStatusCode)status, Problem(type, status));
        var output = new CapturingOutput();

        var exit = await RunAsync(server, output, rotate: true, yes: true);

        Assert.Equal(DeviceRevocation.ExitRevokedRotationNotStarted, exit);
        Assert.True(server.Saw($"DELETE {DevicePath}"), "the revoke is never rolled back");
        var (message, code) = Assert.Single(output.Errors);
        Assert.Equal(type, code);
        Assert.Contains(saying, message, StringComparison.Ordinal);
        Assert.Contains(output.Successes, s => s.StartsWith("Device revoked", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_409_nothing_owed_is_not_a_failure()
    {
        var server = new Server()
            .On($"GET {DevicePath}/key-exposure", HttpStatusCode.OK, Exposure())
            .On($"DELETE {DevicePath}", HttpStatusCode.NoContent)
            .On($"POST {DevicePath}/key-rotation", HttpStatusCode.Conflict, Problem("nothing-owed", 409));

        var exit = await RunAsync(server, new CapturingOutput(), rotate: true, yes: true);

        Assert.Equal(DeviceRevocation.ExitOk, exit);
    }

    [Fact]
    public async Task A_device_that_is_not_found_exits_1_and_keeps_the_existing_message()
    {
        var server = new Server()
            .On($"GET {DevicePath}/key-exposure", HttpStatusCode.NotFound, Problem("not-found", 404));
        var output = new CapturingOutput();

        var exit = await RunAsync(server, output, yes: true);

        Assert.Equal(DeviceRevocation.ExitFailure, exit);
        Assert.Equal(("No such device, or it is not yours to revoke.", "not-found"), Assert.Single(output.Errors));
    }

    // ── the preview and the limits copy ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_preview_names_the_projects_and_environments_and_states_the_limits()
    {
        var output = new CapturingOutput();

        await RunAsync(HealthyServer(), output, yes: true);

        Assert.Contains(output.Infos, i => i.Contains("Alpha (dev)", StringComparison.Ordinal));
        var text = string.Join("\n", output.Infos);
        Assert.Contains("stops access, not knowledge", text, StringComparison.Ordinal);
        Assert.Contains("does not make the device forget", text, StringComparison.Ordinal);
        Assert.Contains("Rotate the secrets themselves", text, StringComparison.Ordinal);
        Assert.Contains("keep the sealing they were written with", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Organisation-level records", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_organisation_key_note_appears_only_when_the_device_used_it()
    {
        var output = new CapturingOutput();

        await RunAsync(HealthyServer(Exposure(usedOrganisationKey: true)), output, yes: true);

        var text = string.Join("\n", output.Infos);
        Assert.Contains("provider credentials", text, StringComparison.Ordinal);
        Assert.Contains("webhook and notification secrets", text, StringComparison.Ordinal);
        Assert.Contains("audit-stream and alert settings", text, StringComparison.Ordinal);
        Assert.Contains("NOT rotated", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_device_that_received_no_keys_says_nothing_to_rotate()
    {
        var output = new CapturingOutput();
        var emptyProjects = $$"""{"deviceId":"{{DeviceId}}","fingerprint":"{{Fingerprint}}","state":"active","usedOrganisationKey":false,"projectCount":0,"owedCount":0,"widenedProjectCount":0,"projects":[]}""";

        await RunAsync(HealthyServer(emptyProjects), output, yes: true);

        Assert.Contains("This device received no keys: nothing to rotate.", output.Infos);
    }

    [Fact]
    public async Task An_unreadable_preview_is_named_and_the_revoke_is_still_offered_but_no_rotation_is_asked()
    {
        var server = new Server()
            .On($"GET {DevicePath}/key-exposure", HttpStatusCode.ServiceUnavailable, Problem("key-store-unreadable", 503))
            .On($"DELETE {DevicePath}", HttpStatusCode.NoContent);
        var output = new CapturingOutput();
        var asked = new List<string>();

        var exit = await RunAsync(server, output, interactive: true, confirm: q => { asked.Add(q); return true; });

        Assert.Equal(DeviceRevocation.ExitOk, exit);
        Assert.Contains(output.Infos, i => i.Contains("key-store-unreadable", StringComparison.Ordinal));
        Assert.Equal(["Revoke device " + DeviceId + "?"], asked);
        Assert.True(server.Saw($"DELETE {DevicePath}"));
        Assert.False(server.Saw("POST"));
    }

    // ── -o json ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Json_writes_exactly_one_document_and_nothing_else_and_never_asks()
    {
        var output = new CapturingOutput();

        // `interactive: true` and a throwing confirm: JSON mode must refuse to ask even on a terminal.
        var exit = await RunAsync(HealthyServer(), output, rotate: true, json: true, interactive: true);

        Assert.Equal(DeviceRevocation.ExitOk, exit);
        Assert.False(output.SaidAnything, "JSON mode owns stdout: no progress, no success line");
        using var document = JsonDocument.Parse(Assert.Single(output.Documents));
        var root = document.RootElement;
        Assert.Equal(DeviceId.ToString(), root.GetProperty("device").GetProperty("id").GetString());
        Assert.Equal(Fingerprint, root.GetProperty("device").GetProperty("fingerprint").GetString());
        Assert.True(root.GetProperty("revoked").GetBoolean());
        Assert.Equal("Alpha", root.GetProperty("exposure").GetProperty("projects")[0].GetProperty("projectName").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("exposureError").ValueKind);
        var rotation = root.GetProperty("rotation");
        Assert.True(rotation.GetProperty("requested").GetBoolean());
        Assert.True(rotation.GetProperty("started").GetBoolean());
        Assert.Equal(RunId.ToString(), rotation.GetProperty("run").GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, rotation.GetProperty("error").ValueKind);
    }

    [Fact]
    public async Task Json_names_a_refused_rotation_in_the_document_and_exits_2()
    {
        var server = new Server()
            .On($"GET {DevicePath}/key-exposure", HttpStatusCode.OK, Exposure())
            .On($"DELETE {DevicePath}", HttpStatusCode.NoContent)
            .On($"POST {DevicePath}/key-rotation", HttpStatusCode.Forbidden, Problem("rotation-not-permitted", 403));
        var output = new CapturingOutput();

        var exit = await RunAsync(server, output, rotate: true, json: true);

        Assert.Equal(DeviceRevocation.ExitRevokedRotationNotStarted, exit);
        using var document = JsonDocument.Parse(Assert.Single(output.Documents));
        var root = document.RootElement;
        Assert.True(root.GetProperty("revoked").GetBoolean());
        var rotation = root.GetProperty("rotation");
        Assert.True(rotation.GetProperty("requested").GetBoolean());
        Assert.False(rotation.GetProperty("started").GetBoolean());
        Assert.Equal(JsonValueKind.Null, rotation.GetProperty("run").ValueKind);
        Assert.Equal("rotation-not-permitted", rotation.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Json_carries_an_unreadable_preview_as_exposureError_and_a_null_exposure()
    {
        var server = new Server()
            .On($"GET {DevicePath}/key-exposure", HttpStatusCode.ServiceUnavailable, Problem("access-record-unreadable", 503))
            .On($"DELETE {DevicePath}", HttpStatusCode.NoContent);
        var output = new CapturingOutput();

        var exit = await RunAsync(server, output, json: true);

        Assert.Equal(DeviceRevocation.ExitOk, exit);
        using var document = JsonDocument.Parse(Assert.Single(output.Documents));
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("exposure").ValueKind);
        Assert.Equal("access-record-unreadable", root.GetProperty("exposureError").GetProperty("code").GetString());
        Assert.False(root.GetProperty("rotation").GetProperty("requested").GetBoolean());
    }
}
