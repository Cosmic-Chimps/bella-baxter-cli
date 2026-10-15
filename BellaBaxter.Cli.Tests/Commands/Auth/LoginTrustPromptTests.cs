using BellaBaxter.Cli.Tests.Services;
using BellaCli.Commands;
using BellaCli.Commands.Auth;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;

namespace BellaBaxter.Cli.Tests.Commands.Auth;

/// <summary>
/// #1317 — <c>bella login</c> (browser) with a <c>.bella</c> that names an unknown server crashed with
/// "Trying to run one or more interactive functions concurrently": the first read of the server address
/// ran the trust question while <c>HumanOutputWriter.StatusAsync</c>'s spinner was live, and Spectre allows
/// one interactive display at a time.
/// </summary>
/// <remarks>
/// <para><c>BellaFileServerTrustTests</c> injects the confirmer and a status-less output writer, so it
/// bypasses exactly the two things that collide. This suite uses the REAL <see cref="HumanOutputWriter"/>
/// (a real <c>AnsiConsole.Status()</c>) and the REAL <see cref="OriginTrustPrompt"/> question, both writing
/// to a Spectre <see cref="TestConsole"/> installed as <c>AnsiConsole.Console</c> — the smallest seam that
/// keeps the real ordering of prompt and spinner. The only thing faked is whether there is a terminal
/// (<see cref="OriginTrustPrompt.CreateConfirmer"/>'s predicate): xunit has no TTY.</para>
/// <para>Mutates the working directory, environment variables and the static console, so it runs alone.</para>
/// </remarks>
[Collection(ApiUrlEnvironmentCollection.Name)]
public class LoginTrustPromptTests : IDisposable
{
    private const string Saas = "https://api.bella-baxter.io";
    private const string Lookalike = "https://bella-baxter.evil.example";

    private static readonly string[] Variables = ["BELLA_BAXTER_URL", "BAXTER_URL", "BELLA_BAXTER_API_KEY", "BELLA_API_KEY"];

    private readonly string _dir;
    private readonly string _configFile;
    private readonly string _originalCwd;
    private readonly IAnsiConsole _originalConsole;
    private readonly Dictionary<string, string?> _original = new();
    private readonly StringWriter _written = new();
    private readonly TestConsoleInput _input = new();
    private readonly IAnsiConsole _console;

    public LoginTrustPromptTests()
    {
        _originalCwd = Directory.GetCurrentDirectory();
        foreach (var v in Variables)
        {
            _original[v] = Environment.GetEnvironmentVariable(v);
            Environment.SetEnvironmentVariable(v, null);
        }

        _dir = Path.Combine(Path.GetTempPath(), "bella-login-trust-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configFile = Path.Combine(_dir, "config.json");
        Directory.SetCurrentDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, ".bella"), $"project = \"p\"\nenvironment = \"e\"\nurl = \"{Lookalike}\"\n");

        // A REAL console, so Spectre's real exclusivity rule applies (TestConsole does not enforce it and
        // therefore cannot see this bug), writing to a string and reading its answers from a scripted input.
        var real = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.Yes,
            Out = new AnsiConsoleOutput(_written),
        });
        real.Profile.Width = 500;
        _console = new ScriptedInputConsole(real, _input);
        _originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = _console;
    }

    public void Dispose()
    {
        AnsiConsole.Console = _originalConsole;
        Directory.SetCurrentDirectory(_originalCwd);
        foreach (var (k, v) in _original)
            Environment.SetEnvironmentVariable(k, v);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private (LoginCommand Command, RecordingHandler Handler, ConfigService Config) Build(bool terminal = true)
    {
        var config = new ConfigService(new BellaConfig(Saas), _configFile)
        {
            OriginConfirmer = OriginTrustPrompt.CreateConfirmer(() => terminal),
            StoredCredentialOrigins = () => [],
        };
        var handler = new RecordingHandler();
        var credentials = new NoCredentials();
        var auth = new AuthService(config, credentials, new HttpClient(handler));
        var keyContext = new KeyContextService(new BellaClientProvider(config, credentials, new GlobalSettings(), auth), credentials);
        var command = new LoginCommand(auth, credentials, keyContext, new ZkeService(), config, new HumanOutputWriter());
        return (command, handler, config);
    }

    private Task<int> RunBrowserLogin(LoginCommand command) =>
        ((ICommand)command).ExecuteAsync(
            new CommandContext([], new EmptyRemaining(), "login", null),
            new LoginSettings(),
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task The_question_is_asked_outside_the_spinner_and_Enter_means_No()
    {
        var (command, handler, config) = Build();
        _input.PushTextWithEnter(string.Empty); // Enter: the default, which must be No

        var exit = await RunBrowserLogin(command);
        var shown = _written.ToString();

        Assert.DoesNotContain("concurrently", shown, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"Trust {Lookalike} and remember it?", shown, StringComparison.Ordinal);
        Assert.Contains(Lookalike, shown, StringComparison.Ordinal);                  // the server
        Assert.Contains(Path.Combine(Path.GetFileName(_dir), ".bella"), shown, StringComparison.Ordinal); // the file
        Assert.Contains(Saas, shown, StringComparison.Ordinal);                       // the configured server
        Assert.Equal(1, exit);
        Assert.Contains("did not trust", shown, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);                                               // nothing reached the host
        Assert.Empty(config.TrustedOrigins);
    }

    [Fact]
    public async Task Answering_yes_asks_first_remembers_it_and_only_then_contacts_the_server()
    {
        var (command, handler, config) = Build();
        _input.PushTextWithEnter("y");

        // The handler refuses every request, so the run ends right after discovery is attempted — before
        // any browser is opened — which is exactly what lets the test see the ORDER: question, then request.
        var exit = await RunBrowserLogin(command);

        Assert.DoesNotContain("concurrently", _written.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, exit);
        Assert.Equal([Lookalike], config.TrustedOrigins);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(Lookalike, request!.GetLeftPart(UriPartial.Authority));
    }

    [Fact]
    public async Task Without_a_terminal_it_refuses_without_asking_and_sends_nothing()
    {
        var (command, handler, config) = Build(terminal: false);

        var exit = await RunBrowserLogin(command);

        Assert.Equal(1, exit);
        Assert.DoesNotContain("Trust ", _written.ToString(), StringComparison.Ordinal); // no question
        Assert.Contains("bella config trust", _written.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
        Assert.Empty(config.TrustedOrigins);
    }

    [Fact]
    public async Task Auth_oidc_asks_outside_the_spinner_before_the_exchange_is_sent()
    {
        // A second command whose FIRST read of the server address sat inside StatusAsync (#1317 survey).
        var config = new ConfigService(new BellaConfig(Saas), _configFile)
        {
            OriginConfirmer = OriginTrustPrompt.CreateConfirmer(() => true),
            StoredCredentialOrigins = () => [],
        };
        var handler = new RecordingHandler();
        var command = new AuthOidcCommand(
            new WorkloadIdentityService(new HttpClient(handler), config), config, new HumanOutputWriter());
        _input.PushTextWithEnter(string.Empty); // Enter: No

        var thrown = await Record.ExceptionAsync(() =>
            ((ICommand)command).ExecuteAsync(
                new CommandContext([], new EmptyRemaining(), "oidc", null),
                new AuthOidcSettings { Token = "t", Tenant = "acme", Project = "p", Env = "e" },
                TestContext.Current.CancellationToken));

        Assert.IsType<UntrustedBellaServerException>(thrown); // not the "concurrently" InvalidOperationException
        Assert.Contains($"Trust {Lookalike} and remember it?", _written.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    /// <summary>A real console whose only difference is where the answers come from.</summary>
    private sealed class ScriptedInputConsole(IAnsiConsole inner, IAnsiConsoleInput input) : IAnsiConsole
    {
        public Profile Profile => inner.Profile;
        public IAnsiConsoleCursor Cursor => inner.Cursor;
        public IAnsiConsoleInput Input => input;
        public IExclusivityMode ExclusivityMode => inner.ExclusivityMode;
        public RenderPipeline Pipeline => inner.Pipeline;
        public void Clear(bool home) => inner.Clear(home);
        public void Write(IRenderable renderable) => inner.Write(renderable);
        public void WriteAnsi(Action<AnsiWriter> action) => inner.WriteAnsi(action);
    }

    private sealed class NoCredentials : CredentialStore
    {
        public override StoredTokens? LoadTokens() => null;
        public override StoredApiKey? LoadApiKey() => null;
    }

    private sealed class EmptyRemaining : IRemainingArguments
    {
        public ILookup<string, string?> Parsed => Array.Empty<string>().ToLookup(x => x, x => (string?)null);
        public IReadOnlyList<string> Raw => [];
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<Uri?> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri);
            throw new HttpRequestException($"stop: no server in this test ({request.RequestUri})");
        }
    }
}
