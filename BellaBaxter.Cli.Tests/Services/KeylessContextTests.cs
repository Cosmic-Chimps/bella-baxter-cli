using BellaCli.Commands.Auth;
using BellaCli.Commands.Exec;
using BellaCli.Commands.Run;
using BellaCli.Commands.Sdk;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console.Cli;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// #1370 — the documented keyless pattern <c>bella run -p &lt;project&gt; -e &lt;env&gt; -- cmd</c> did not work on its own.
/// </summary>
/// <remarks>
/// <para>Two failures: with no context the exchange was sent with empty slugs (server: 422 <c>missing_context</c>) and
/// <c>bella run</c> then said "Not logged in"; with a context naming ANOTHER project or environment, <c>-p/-e</c> were
/// silently dropped and the exchange targeted the other one.</para>
/// <para>The suites below drive the real resolution (a real <c>.bella</c> in a temp directory, real environment
/// variables) and the real commands, faking only the machine-dependent edges: which platform this is, the CI provider's
/// token, and the exchange call itself, which records what it was asked for. They mutate the working directory and the
/// environment, so they run alone.</para>
/// </remarks>
[Collection(ApiUrlEnvironmentCollection.Name)]
public abstract class KeylessFixture : IDisposable
{
    private static readonly string[] Variables =
    [
        "BELLA_BAXTER_PROJECT", "BELLA_PROJECT", "BELLA_BAXTER_ENV", "BELLA_ENV", "BELLA_BAXTER_TENANT",
        "BELLA_BAXTER_API_KEY", "BELLA_API_KEY", "BELLA_BAXTER_ACCESS_TOKEN",
        "BELLA_BAXTER_URL", "BAXTER_URL", "BELLA_OIDC_AUDIENCE",
        // Never let a test run on a CI runner touch the runner's own export file.
        "GITHUB_ENV", "ACTIONS_ID_TOKEN_REQUEST_URL", "ACTIONS_ID_TOKEN_REQUEST_TOKEN",
        // Spec 085 — the exported device key, and the GitLab markers the export branches on.
        "BELLA_BAXTER_PRIVATE_KEY", "GITLAB_CI", "CI_JOB_JWT_V2", "BELLA_TEST_CHILD_OUT",
    ];

    private readonly string _originalCwd = Directory.GetCurrentDirectory();
    private readonly Dictionary<string, string?> _original = new();
    protected readonly string Dir;

    protected KeylessFixture()
    {
        foreach (var name in Variables)
        {
            _original[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }

        Dir = Path.Combine(Path.GetTempPath(), "bella-keyless-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        Directory.SetCurrentDirectory(Dir);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalCwd);
        foreach (var (name, value) in _original)
            Environment.SetEnvironmentVariable(name, value);
        try { Directory.Delete(Dir, recursive: true); } catch { /* best effort */ }
    }

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected static void Env(string name, string value) => Environment.SetEnvironmentVariable(name, value);

    protected void BellaFile(string contents) => File.WriteAllText(Path.Combine(Dir, ".bella"), contents);

    protected ConfigService NewConfig() =>
        new(new BellaConfig("https://api.bella-baxter.io"), Path.Combine(Dir, "config.json"));

    protected FakeKeyless NewKeyless(WorkloadPlatform platform = WorkloadPlatform.GitHubActions) =>
        new(NewConfig()) { Platform = platform };

    protected static CommandContext NoRemaining(string name) => new([], new EmptyRemaining(), name, null);

    protected static StoredTokens Login(string orgSlug) =>
        new("access", "refresh", DateTimeOffset.UtcNow.AddHours(1), OrgSlug: orgSlug);

    /// <summary>Only the edges of the keyless flow are fake; resolution of the slugs is the real code.</summary>
    protected sealed class FakeKeyless(ConfigService config) : WorkloadIdentityService(new HttpClient(), config)
    {
        public WorkloadPlatform Platform { get; set; } = WorkloadPlatform.GitHubActions;
        public string? KeyInEnvironment { get; set; }
        public string? OidcToken { get; set; } = "oidc-token";
        public OidcExchangeResult? IssuedKey { get; set; } = new("bax-issued", DateTimeOffset.UtcNow.AddMinutes(10));
        public List<(string? Tenant, string? Project, string? Environment)> Exchanges { get; } = [];

        /// <summary>Spec 085 — the public key each exchange was sent (null: none was).</summary>
        public List<string?> PublicKeys { get; } = [];

        /// <summary>Spec 085 — what the server refused with, when a test needs it to refuse.</summary>
        public Exception? ExchangeThrows { get; set; }

        internal override WorkloadPlatform CurrentPlatform() => Platform;
        internal override string? ApiKeyInEnvironment() => KeyInEnvironment;

        public override Task<string?> GetOidcTokenAsync(string? audience = null, CancellationToken ct = default) =>
            Task.FromResult(OidcToken);

        public override Task<OidcExchangeResult?> ExchangeGlobalAsync(
            string oidcToken, string? tenantSlug = null, string? projectSlug = null, string? environmentSlug = null,
            CancellationToken ct = default, string? publicKey = null)
        {
            Exchanges.Add((tenantSlug, projectSlug, environmentSlug));
            PublicKeys.Add(publicKey);
            if (ExchangeThrows is not null)
                throw ExchangeThrows;
            return Task.FromResult(IssuedKey);
        }
    }

    /// <summary>Holds exactly what the test gives it — never the developer's real login.</summary>
    protected sealed class FakeCredentials(StoredTokens? tokens = null) : CredentialStore
    {
        public override StoredTokens? LoadTokens() => tokens;
        public override StoredApiKey? LoadApiKey() => null;
    }

    protected sealed class RecordingOutput : IOutputWriter
    {
        public List<(string Message, string? Code)> Errors { get; } = [];
        public string AllErrors => string.Join("\n", Errors.Select(e => e.Message));

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

    private sealed class EmptyRemaining : IRemainingArguments
    {
        public ILookup<string, string?> Parsed => Array.Empty<string>().ToLookup(_ => string.Empty, _ => (string?)null);
        public IReadOnlyList<string> Raw => [];
    }
}

/// <summary>What the keyless flow resolves, and what it does when it cannot.</summary>
[Collection(ApiUrlEnvironmentCollection.Name)]
public class KeylessContextResolutionTests : KeylessFixture
{
    private static readonly SlugField[] All = [SlugField.Tenant, SlugField.Project, SlugField.Environment];

    // ── a missing context stops BEFORE the exchange, and names what is missing ──────────────────────────────

    [Fact]
    public async Task No_context_at_all_does_not_call_the_exchange_and_names_all_three()
    {
        var keyless = NewKeyless();

        var outcome = await keyless.TryAutoExchangeAsync(ct: Ct);

        var missing = Assert.IsType<KeylessExchange.MissingContext>(outcome);
        Assert.Equal(All, missing.Missing);
        Assert.Equal(WorkloadPlatform.GitHubActions, missing.Platform);
        Assert.Empty(keyless.Exchanges);
    }

    [Theory]
    [InlineData(null, "proj", "env", SlugField.Tenant)]
    [InlineData("acme", null, "env", SlugField.Project)]
    [InlineData("acme", "proj", null, SlugField.Environment)]
    [InlineData("  ", "proj", "env", SlugField.Tenant)] // blank is absent
    public async Task Each_single_missing_field_is_named_on_its_own(string? tenant, string? project, string? env, SlugField expected)
    {
        var keyless = NewKeyless();

        var outcome = await keyless.TryAutoExchangeAsync(tenant, project, env, ct: Ct);

        var missing = Assert.IsType<KeylessExchange.MissingContext>(outcome);
        Assert.Equal([expected], missing.Missing);
        Assert.Empty(keyless.Exchanges);
    }

    [Fact]
    public async Task A_bella_file_with_no_org_leaves_the_tenant_missing()
    {
        BellaFile("project = \"proj\"\nenvironment = \"env\"\n");
        var keyless = NewKeyless();

        var outcome = await keyless.TryAutoExchangeAsync(ct: Ct);

        Assert.Equal([SlugField.Tenant], Assert.IsType<KeylessExchange.MissingContext>(outcome).Missing);
        Assert.Empty(keyless.Exchanges);
    }

    [Fact]
    public void The_message_names_each_missing_field_and_how_to_supply_it()
    {
        var message = new KeylessExchange.MissingContext(
            WorkloadPlatform.GitHubActions, [SlugField.Tenant, SlugField.Environment]).Describe(includeLoginHint: true);

        Assert.Contains("GitHubActions", message);
        Assert.Contains("--tenant", message);
        Assert.Contains("BELLA_BAXTER_TENANT", message);
        Assert.Contains("org", message);
        Assert.Contains("--env", message);
        Assert.Contains("BELLA_BAXTER_ENV", message);
        Assert.Contains(".bella", message);
        Assert.Contains("bella login", message);
        // Only what is missing — a reader must not wonder whether the project is also a problem.
        Assert.DoesNotContain("--project", message);
        Assert.DoesNotContain("BELLA_BAXTER_PROJECT", message);
        Assert.DoesNotContain("Not logged in", message);
    }

    [Fact]
    public void The_login_hint_is_left_out_where_there_is_no_login_to_fall_back_to()
    {
        var message = new KeylessExchange.MissingContext(
            WorkloadPlatform.Kubernetes, [SlugField.Project]).Describe(includeLoginHint: false);

        Assert.Contains("--project", message);
        Assert.DoesNotContain("bella login", message);
    }

    // ── what is NOT keyless stays out of the way ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Not_a_workload_platform_is_not_applicable_even_with_no_context()
    {
        var keyless = NewKeyless();
        keyless.Platform = WorkloadPlatform.None;

        Assert.IsType<KeylessExchange.NotApplicable>(await keyless.TryAutoExchangeAsync(ct: Ct));
        Assert.Empty(keyless.Exchanges);
    }

    [Fact]
    public async Task A_key_already_in_the_environment_is_not_applicable_even_with_no_context()
    {
        var keyless = NewKeyless();
        keyless.KeyInEnvironment = "bax-already";

        Assert.IsType<KeylessExchange.NotApplicable>(await keyless.TryAutoExchangeAsync(ct: Ct));
        Assert.Empty(keyless.Exchanges);
    }

    [Fact]
    public async Task No_oidc_token_is_not_applicable_even_with_no_context()
    {
        var keyless = NewKeyless();
        keyless.OidcToken = null;

        Assert.IsType<KeylessExchange.NotApplicable>(await keyless.TryAutoExchangeAsync(ct: Ct));
        Assert.Empty(keyless.Exchanges);
    }

    [Fact]
    public async Task A_rejected_exchange_is_failed_not_missing_context()
    {
        var keyless = NewKeyless();
        keyless.IssuedKey = null;

        var outcome = await keyless.TryAutoExchangeAsync("acme", "proj", "env", ct: Ct);

        Assert.IsType<KeylessExchange.Failed>(outcome);
        Assert.Single(keyless.Exchanges);
    }

    [Fact]
    public async Task A_complete_context_is_exchanged_and_the_key_returned()
    {
        var keyless = NewKeyless();

        var outcome = await keyless.TryAutoExchangeAsync("acme", "proj", "env", ct: Ct);

        Assert.Equal("bax-issued", Assert.IsType<KeylessExchange.Exchanged>(outcome).Key.Token);
        Assert.Equal([("acme", "proj", "env")], keyless.Exchanges);
    }

    // ── -t/-p/-e win over env vars and .bella, field by field ───────────────────────────────────────────────

    [Fact]
    public async Task Only_the_environment_flag_given_takes_the_project_from_the_environment_variables()
    {
        Env("BELLA_BAXTER_TENANT", "env-tenant");
        Env("BELLA_BAXTER_PROJECT", "env-project");
        Env("BELLA_BAXTER_ENV", "env-env");
        var keyless = NewKeyless();

        await keyless.TryAutoExchangeAsync(explicitEnvironment: "flag-env", ct: Ct);

        Assert.Equal([("env-tenant", "env-project", "flag-env")], keyless.Exchanges);
    }

    [Fact]
    public async Task Only_the_project_flag_given_takes_the_rest_from_a_bella_file()
    {
        BellaFile("org = \"file-org\"\nproject = \"file-project\"\nenvironment = \"file-env\"\n");
        var keyless = NewKeyless();

        await keyless.TryAutoExchangeAsync(explicitProject: "flag-project", ct: Ct);

        Assert.Equal([("file-org", "flag-project", "file-env")], keyless.Exchanges);
    }

    [Fact]
    public async Task Flags_win_over_a_context_that_names_a_different_project_and_environment()
    {
        // The "silently targets another environment" case: the user typed production, a stale .bella says dev.
        BellaFile("org = \"file-org\"\nproject = \"file-project\"\nenvironment = \"dev\"\n");
        Env("BELLA_BAXTER_PROJECT", "env-project");
        Env("BELLA_BAXTER_ENV", "env-dev");
        var keyless = NewKeyless();

        await keyless.TryAutoExchangeAsync("flag-tenant", "flag-project", "production", ct: Ct);

        Assert.Equal([("flag-tenant", "flag-project", "production")], keyless.Exchanges);
    }

    [Fact]
    public async Task A_tenant_flag_wins_over_the_tenant_variable_and_the_bella_org()
    {
        BellaFile("org = \"file-org\"\nproject = \"p\"\nenvironment = \"e\"\n");
        Env("BELLA_BAXTER_TENANT", "env-tenant");
        var keyless = NewKeyless();

        await keyless.TryAutoExchangeAsync(explicitTenant: "flag-tenant", ct: Ct);

        Assert.Equal("flag-tenant", Assert.Single(keyless.Exchanges).Tenant);
    }

    [Fact]
    public async Task Without_a_tenant_flag_the_tenant_comes_from_the_environment_then_the_bella_org()
    {
        BellaFile("org = \"file-org\"\nproject = \"p\"\nenvironment = \"e\"\n");
        var keyless = NewKeyless();
        await keyless.TryAutoExchangeAsync(ct: Ct);
        Assert.Equal("file-org", Assert.Single(keyless.Exchanges).Tenant);

        Env("BELLA_BAXTER_PROJECT", "ep");
        Env("BELLA_BAXTER_ENV", "ee");
        Env("BELLA_BAXTER_TENANT", "env-tenant");
        keyless.Exchanges.Clear();
        await keyless.TryAutoExchangeAsync(ct: Ct);
        Assert.Equal([("env-tenant", "ep", "ee")], keyless.Exchanges);
    }

    [Fact]
    public async Task The_environment_variables_still_outrank_a_bella_file_when_no_flag_is_given()
    {
        // The order ResolveContextWithOrg already implements — field-by-field merging must not reorder it.
        BellaFile("org = \"file-org\"\nproject = \"file-project\"\nenvironment = \"file-env\"\n");
        Env("BELLA_BAXTER_PROJECT", "env-project");
        Env("BELLA_BAXTER_ENV", "env-env");
        Env("BELLA_BAXTER_TENANT", "env-tenant");
        var keyless = NewKeyless();

        await keyless.TryAutoExchangeAsync(ct: Ct);

        Assert.Equal([("env-tenant", "env-project", "env-env")], keyless.Exchanges);
    }

    [Fact]
    public async Task A_lone_project_variable_with_the_other_two_given_as_flags_is_used()
    {
        // BELLA_BAXTER_PROJECT=api bella run -t acme -e prod — the half-set variables are the user's own words.
        Env("BELLA_BAXTER_PROJECT", "api");
        var keyless = NewKeyless();

        await keyless.TryAutoExchangeAsync(explicitTenant: "acme", explicitEnvironment: "prod", ct: Ct);

        Assert.Equal([("acme", "api", "prod")], keyless.Exchanges);
    }

    // ── security: .bella supplies the three slugs and nothing else ─────────────────────────────────────────

    [Fact]
    public async Task A_bella_file_cannot_change_the_audience_or_token_source_of_the_exchange()
    {
        // KeylessIgnoresProjectFileTests pins the token path and the audience resolver. This pins that merging the
        // slugs did not give .bella a way in: a file naming both still leaves only the three slugs to be read.
        BellaFile(
            "org = \"o\"\nproject = \"p\"\nenvironment = \"e\"\noidc_audience = \"https://sts.amazonaws.com\"\nnode_token_path = \"/home/dev/.ssh/id_ed25519\"\n");

        Assert.Equal(WorkloadIdentityService.DefaultOidcAudience, WorkloadIdentityService.ResolveOidcAudience(null));
        var keyless = NewKeyless();
        await keyless.TryAutoExchangeAsync(ct: Ct);

        Assert.Equal([("o", "p", "e")], keyless.Exchanges);
    }
}

/// <summary>Every command that auto-exchanges: what each does with each outcome.</summary>
[Collection(ApiUrlEnvironmentCollection.Name)]
public class KeylessCommandsTests : KeylessFixture
{
    // ── bella run ───────────────────────────────────────────────────────────────────────────────────────────

    private static RunCommand NewRun(FakeKeyless keyless, CredentialStore credentials, RecordingOutput output) =>
        // Provider, context and ZKE selection are only reached once a client is chosen; every test below ends before.
        new(null!, credentials, null!, keyless, null!, output);

    private static Task<int> Run(RunCommand command, RunCommand.Settings settings) =>
        ((ICommand)command).ExecuteAsync(NoRemaining("run"), settings, Ct);

    [Fact]
    public void Bella_run_declares_the_tenant_option_exactly_as_exec_and_sdk_run_do()
    {
        string Template(Type settings) => settings.GetProperty("Tenant")!
            .GetCustomAttributes(typeof(CommandOptionAttribute), false).Cast<CommandOptionAttribute>().Single().ToString()!;
        string Description(Type settings) => settings.GetProperty("Tenant")!
            .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
            .Cast<System.ComponentModel.DescriptionAttribute>().Single().Description;

        Assert.Equal(Template(typeof(ExecCommand.Settings)), Template(typeof(RunCommand.Settings)));
        Assert.Equal(Template(typeof(SdkRunCommand.Settings)), Template(typeof(RunCommand.Settings)));
        Assert.Equal(Description(typeof(ExecCommand.Settings)), Description(typeof(RunCommand.Settings)));
    }

    [Fact]
    public void The_short_options_of_each_auto_exchanging_command_are_unique()
    {
        foreach (var settings in new[] { typeof(RunCommand.Settings), typeof(ExecCommand.Settings), typeof(SdkRunCommand.Settings) })
        {
            var shorts = settings.GetProperties()
                .SelectMany(p => p.GetCustomAttributes(typeof(CommandOptionAttribute), false).Cast<CommandOptionAttribute>())
                .SelectMany(o => o.ShortNames)
                .ToList();
            Assert.Equal(shorts.Count, shorts.Distinct().Count());
            Assert.Contains("t", shorts);
        }
    }

    [Fact]
    public async Task Run_without_context_and_without_a_login_names_the_missing_fields_and_does_not_say_not_logged_in()
    {
        var keyless = NewKeyless();
        var output = new RecordingOutput();

        var exit = await Run(NewRun(keyless, new FakeCredentials(), output), new RunCommand.Settings { Cmd = ["true"] });

        Assert.Equal(1, exit);
        var error = Assert.Single(output.Errors);
        Assert.Equal("missing_context", error.Code);
        Assert.Contains("--tenant", error.Message);
        Assert.Contains("--project", error.Message);
        Assert.Contains("--env", error.Message);
        Assert.DoesNotContain("Not logged in", error.Message);
        Assert.Empty(keyless.Exchanges);
    }

    [Fact]
    public async Task Run_with_only_the_environment_missing_names_only_the_environment()
    {
        var keyless = NewKeyless();
        var output = new RecordingOutput();

        var exit = await Run(
            NewRun(keyless, new FakeCredentials(), output),
            new RunCommand.Settings { Tenant = "acme", Project = "api", Cmd = ["true"] });

        Assert.Equal(1, exit);
        Assert.Contains("--env", output.AllErrors);
        Assert.DoesNotContain("--tenant", output.AllErrors);
        Assert.DoesNotContain("--project", output.AllErrors);
        Assert.Empty(keyless.Exchanges);
    }

    [Fact]
    public async Task Run_gives_its_tenant_flag_to_the_exchange()
    {
        var keyless = NewKeyless();
        keyless.IssuedKey = null; // rejected: ends the run before a client is built
        var output = new RecordingOutput();

        await Run(
            NewRun(keyless, new FakeCredentials(), output),
            new RunCommand.Settings { Tenant = "flag-tenant", Project = "flag-project", Environment = "flag-env", Cmd = ["true"] });

        Assert.Equal([("flag-tenant", "flag-project", "flag-env")], keyless.Exchanges);
    }

    [Fact]
    public async Task Run_takes_the_tenant_from_the_environment_when_the_flag_is_absent()
    {
        Env("BELLA_BAXTER_TENANT", "env-tenant");
        var keyless = NewKeyless();
        keyless.IssuedKey = null;

        await Run(
            NewRun(keyless, new FakeCredentials(), new RecordingOutput()),
            new RunCommand.Settings { Project = "flag-project", Environment = "flag-env", Cmd = ["true"] });

        Assert.Equal([("env-tenant", "flag-project", "flag-env")], keyless.Exchanges);
    }

    [Fact]
    public async Task Run_with_a_stored_login_and_no_context_falls_back_to_the_stored_login()
    {
        // A self-hosted runner that is also logged in must not regress: no new refusal, no new output. The command
        // gets past the gate to the stored-login client — which this test deliberately leaves unwired, so the only
        // thing it can say is the client-building error, never the keyless one and never "Not logged in".
        var output = new RecordingOutput();

        var exit = await Run(NewRun(NewKeyless(), new FakeCredentials(Login("acme")), output), new RunCommand.Settings { Cmd = ["true"] });

        Assert.Equal(1, exit);
        var error = Assert.Single(output.Errors);
        Assert.StartsWith("Authentication error", error.Message);
        Assert.Null(error.Code);
    }

    [Fact]
    public async Task Run_refuses_a_tenant_flag_that_disagrees_with_the_stored_login_naming_both()
    {
        var keyless = NewKeyless();
        var output = new RecordingOutput();

        var exit = await Run(
            NewRun(keyless, new FakeCredentials(Login("jjchiw-9c45ab")), output),
            new RunCommand.Settings { Tenant = "code4fun-17d1c3", Cmd = ["true"] });

        Assert.Equal(1, exit);
        Assert.Contains("code4fun-17d1c3", output.AllErrors);
        Assert.Contains("jjchiw-9c45ab", output.AllErrors);
        Assert.Contains("bella org switch code4fun-17d1c3", output.AllErrors);
    }

    [Fact]
    public async Task Run_accepts_a_tenant_flag_that_matches_the_stored_login_ignoring_case()
    {
        var output = new RecordingOutput();

        // Past the refusal, to the unwired stored-login client.
        await Run(NewRun(NewKeyless(), new FakeCredentials(Login("Acme")), output), new RunCommand.Settings { Tenant = "acme", Cmd = ["true"] });

        Assert.StartsWith("Authentication error", Assert.Single(output.Errors).Message);
    }

    // ── bella exec ──────────────────────────────────────────────────────────────────────────────────────────

    private ExecCommand NewExec(FakeKeyless keyless, CredentialStore credentials, RecordingOutput output)
    {
        var config = NewConfig();
        return new ExecCommand(credentials, config, new AuthService(config, credentials, new HttpClient()), keyless, output);
    }

    private static Task<int> Exec(ExecCommand command, ExecCommand.Settings settings) =>
        ((ICommand)command).ExecuteAsync(NoRemaining("exec"), settings, Ct);

    [Fact]
    public async Task Exec_without_context_and_without_a_login_names_the_missing_fields()
    {
        var keyless = NewKeyless();
        var output = new RecordingOutput();

        var exit = await Exec(NewExec(keyless, new FakeCredentials(), output), new ExecCommand.Settings { Cmd = ["true"] });

        Assert.Equal(1, exit);
        Assert.Equal("missing_context", Assert.Single(output.Errors).Code);
        Assert.Contains("--tenant", output.AllErrors);
        Assert.DoesNotContain("Not authenticated", output.AllErrors);
        Assert.Empty(keyless.Exchanges);
    }

    [Fact]
    public async Task Exec_gives_all_three_flags_to_the_exchange()
    {
        var keyless = NewKeyless();
        keyless.IssuedKey = null;

        await Exec(
            NewExec(keyless, new FakeCredentials(), new RecordingOutput()),
            new ExecCommand.Settings { Tenant = "t", Project = "p", Environment = "e", Cmd = ["true"] });

        Assert.Equal([("t", "p", "e")], keyless.Exchanges);
    }

    [Fact]
    public async Task Exec_with_a_stored_login_and_no_context_falls_back_to_the_stored_login()
    {
        var output = new RecordingOutput();

        var exit = await Exec(NewExec(NewKeyless(), new FakeCredentials(Login("acme")), output), new ExecCommand.Settings { Cmd = ["true"] });

        // The stored-login path's own message, not the keyless one: it got there.
        Assert.Equal(1, exit);
        Assert.Contains("required for JWT auth", output.AllErrors);
        Assert.DoesNotContain("missing_context", output.Errors.Select(e => e.Code));
    }

    [Fact]
    public async Task Exec_on_the_stored_login_uses_the_typed_project_and_environment_not_the_variables()
    {
        // The stored-login path must not swap context either: -p/-e typed beat BELLA_BAXTER_PROJECT/ENV, field by field.
        Assert.SkipWhen(OperatingSystem.IsWindows(), "spawns sh to read the environment the child was given");
        Env("BELLA_BAXTER_PROJECT", "env-project");
        Env("BELLA_BAXTER_ENV", "env-env");
        var seen = Path.Combine(Dir, "seen.txt");

        var exit = await Exec(
            NewExec(NewKeyless(WorkloadPlatform.None), new FakeCredentials(Login("acme")), new RecordingOutput()),
            new ExecCommand.Settings { Environment = "flag-env", Cmd = ["sh", "-c", $"printf %s \"$BELLA_BAXTER_PROJECT/$BELLA_BAXTER_ENV\" > '{seen}'"] });

        Assert.Equal(0, exit);
        Assert.Equal("env-project/flag-env", await File.ReadAllTextAsync(seen, Ct));
    }

    [Fact]
    public async Task Exec_refuses_a_tenant_flag_that_disagrees_with_the_stored_login_naming_both()
    {
        var output = new RecordingOutput();

        var exit = await Exec(
            NewExec(NewKeyless(WorkloadPlatform.None), new FakeCredentials(Login("jjchiw-9c45ab")), output),
            new ExecCommand.Settings { Tenant = "code4fun-17d1c3", Project = "p", Environment = "e", Cmd = ["true"] });

        Assert.Equal(1, exit);
        Assert.Contains("code4fun-17d1c3", output.AllErrors);
        Assert.Contains("jjchiw-9c45ab", output.AllErrors);
    }

    // ── bella sdk run ───────────────────────────────────────────────────────────────────────────────────────

    private SdkRunCommand NewSdkRun(FakeKeyless keyless, CredentialStore credentials, RecordingOutput output)
    {
        var config = NewConfig();
        return new SdkRunCommand(
            credentials, config, new AuthService(config, credentials, new HttpClient()), keyless, null!, output);
    }

    private static Task<int> SdkRun(SdkRunCommand command, SdkRunCommand.Settings settings) =>
        ((ICommand)command).ExecuteAsync(NoRemaining("sdk run"), settings, Ct);

    [Fact]
    public async Task Sdk_run_without_context_and_without_a_login_names_the_missing_fields()
    {
        var keyless = NewKeyless();
        var output = new RecordingOutput();

        var exit = await SdkRun(NewSdkRun(keyless, new FakeCredentials(), output), new SdkRunCommand.Settings { Cmd = ["true"] });

        Assert.Equal(1, exit);
        Assert.Equal("missing_context", Assert.Single(output.Errors).Code);
        Assert.DoesNotContain("Not authenticated", output.AllErrors);
        Assert.Empty(keyless.Exchanges);
    }

    [Fact]
    public async Task Sdk_run_gives_all_three_flags_to_the_exchange()
    {
        var keyless = NewKeyless();
        keyless.IssuedKey = null;

        await SdkRun(
            NewSdkRun(keyless, new FakeCredentials(), new RecordingOutput()),
            new SdkRunCommand.Settings { Tenant = "t", Project = "p", Environment = "e", Cmd = ["true"] });

        Assert.Equal([("t", "p", "e")], keyless.Exchanges);
    }

    [Fact]
    public async Task Sdk_run_refuses_a_tenant_flag_that_disagrees_with_the_stored_login_naming_both()
    {
        var output = new RecordingOutput();

        var exit = await SdkRun(
            NewSdkRun(NewKeyless(WorkloadPlatform.None), new FakeCredentials(Login("jjchiw-9c45ab")), output),
            new SdkRunCommand.Settings { Tenant = "code4fun-17d1c3", Project = "p", Environment = "e", Cmd = ["true"] });

        Assert.Equal(1, exit);
        Assert.Contains("code4fun-17d1c3", output.AllErrors);
        Assert.Contains("jjchiw-9c45ab", output.AllErrors);
    }

    [Fact]
    public async Task Sdk_run_with_a_stored_login_and_no_context_falls_back_to_the_stored_login()
    {
        var output = new RecordingOutput();

        var exit = await SdkRun(NewSdkRun(NewKeyless(), new FakeCredentials(Login("acme")), output), new SdkRunCommand.Settings { Cmd = ["true"] });

        Assert.Equal(1, exit);
        Assert.Contains("required for JWT auth", output.AllErrors);
    }

    // ── bella auth oidc ─────────────────────────────────────────────────────────────────────────────────────

    private AuthOidcCommand NewAuthOidc(FakeKeyless keyless, RecordingOutput output) =>
        new(keyless, NewConfig(), output);

    private static Task<int> AuthOidc(AuthOidcCommand command, AuthOidcSettings settings) =>
        ((ICommand)command).ExecuteAsync(NoRemaining("auth oidc"), settings, Ct);

    [Fact]
    public async Task Auth_oidc_without_context_names_each_missing_field_and_does_not_call_the_exchange()
    {
        var keyless = NewKeyless();
        var output = new RecordingOutput();

        var exit = await AuthOidc(NewAuthOidc(keyless, output), new AuthOidcSettings { Token = "tok", Tenant = "acme" });

        Assert.Equal(1, exit);
        var error = Assert.Single(output.Errors);
        Assert.Equal("missing_context", error.Code);
        Assert.Contains("--project", error.Message);
        Assert.Contains("--env", error.Message);
        Assert.DoesNotContain("--tenant", error.Message); // given, so not named
        Assert.Empty(keyless.Exchanges);
    }

    [Fact]
    public async Task Auth_oidc_merges_flags_over_the_environment_field_by_field()
    {
        Env("BELLA_BAXTER_TENANT", "env-tenant");
        Env("BELLA_BAXTER_PROJECT", "env-project");
        Env("BELLA_BAXTER_ENV", "env-env");
        var keyless = NewKeyless();
        keyless.IssuedKey = null; // rejected: no export to a CI file

        await AuthOidc(NewAuthOidc(keyless, new RecordingOutput()), new AuthOidcSettings { Token = "tok", Env = "flag-env" });

        Assert.Equal([("env-tenant", "env-project", "flag-env")], keyless.Exchanges);
    }
}
