using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using BellaBaxter.Cli.Tests.Services;
using BellaCli.Commands.Auth;
using BellaCli.Commands.Issue;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console.Cli;

namespace BellaBaxter.Cli.Tests.Commands.Auth;

/// <summary>
/// Spec 085 (US2) — <c>bella auth oidc</c> binds a throwaway device key to the key it mints, and hands the private half
/// to LATER steps without ever echoing it.
/// </summary>
/// <remarks>
/// <para>A tenant that requires registered devices refuses a key that was bound to none, and refuses a read that does
/// not present the bound one. So the sign-in step makes a keypair, sends the public half, and the private half has to
/// reach the next step. On GitHub that is <c>$GITHUB_ENV</c> — and the ORDER is the control: a value written to that
/// file is only masked in the log if the mask command has already been seen, so every secret line is masked first.</para>
/// <para>The private half is worth exactly as much as the API key beside it (it is bound to that one key and dies with
/// it), which is why it travels the same way. Anywhere else it is never printed: a pipeline log is not a secret store.</para>
/// </remarks>
[Collection(ApiUrlEnvironmentCollection.Name)]
public class AuthOidcExportsTheDeviceKeyTests : KeylessFixture
{
    private const string Api = "bax-0123456789abcdef0123456789abcdef-secret";

    private string _githubEnv = string.Empty;

    private void OnGitHub()
    {
        _githubEnv = Path.Combine(Dir, "github_env");
        Env("GITHUB_ENV", _githubEnv);
        Env("ACTIONS_ID_TOKEN_REQUEST_URL", "https://token.actions.example/id");
        Env("ACTIONS_ID_TOKEN_REQUEST_TOKEN", "request-token");
    }

    private static void OnGitLab()
    {
        Env("GITLAB_CI", "true");
        Env("CI_JOB_JWT_V2", "jwt");
    }

    private FakeKeyless Keyless(string key = Api)
    {
        var keyless = NewKeyless();
        keyless.IssuedKey = new OidcExchangeResult(key, DateTimeOffset.UtcNow.AddMinutes(10));
        return keyless;
    }

    private static AuthOidcSettings Settings(bool json = false) =>
        new() { Token = "tok", Tenant = "acme", Project = "p", Env = "e", Json = json };

    private static Task<int> AuthOidc(AuthOidcCommand command, AuthOidcSettings settings) =>
        ((ICommand)command).ExecuteAsync(NoRemaining("auth oidc"), settings, Ct);

    /// <summary>Stdout, line by line, with the size of the export file at the moment each line was printed.</summary>
    private sealed class OrderedStdout(Func<long> exportFileLength) : StringWriter
    {
        public List<(string Line, long ExportFileLength)> Lines { get; } = [];

        public override void WriteLine(string? value)
        {
            Lines.Add((value ?? string.Empty, exportFileLength()));
            base.WriteLine(value);
        }
    }

    private async Task<(int Exit, OrderedStdout Stdout, CapturingOutput Output)> Run(
        FakeKeyless keyless, AuthOidcSettings settings)
    {
        var output = new CapturingOutput();
        var stdout = new OrderedStdout(() => !string.IsNullOrEmpty(_githubEnv) && File.Exists(_githubEnv)
            ? new FileInfo(_githubEnv).Length
            : 0);
        var original = Console.Out;
        Console.SetOut(stdout);
        try
        {
            var exit = await AuthOidc(new AuthOidcCommand(keyless, NewConfig(), output), settings);
            return (exit, stdout, output);
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    private static (string[] Pem, string Delimiter, string ApiKeyLine, string LegacyLine) ReadExport(string path)
    {
        var lines = File.ReadAllLines(path);
        var start = Array.FindIndex(lines, l => l.StartsWith("BELLA_BAXTER_PRIVATE_KEY<<", StringComparison.Ordinal));
        Assert.True(start >= 0, "BELLA_BAXTER_PRIVATE_KEY must be written in the multi-line `<<DELIMITER` form.");
        var delimiter = lines[start]["BELLA_BAXTER_PRIVATE_KEY<<".Length..];
        var end = Array.IndexOf(lines, delimiter, start + 1);
        Assert.True(end > start, "The multi-line value must be closed by its delimiter.");
        return (
            lines[(start + 1)..end],
            delimiter,
            lines.Single(l => l.StartsWith("BELLA_BAXTER_API_KEY=", StringComparison.Ordinal)),
            lines.Single(l => l.StartsWith("BELLA_API_KEY=", StringComparison.Ordinal)));
    }

    private static string PublicHalfOf(string[] pemLines)
    {
        var body = string.Concat(pemLines.Where(l => !l.StartsWith("-----", StringComparison.Ordinal)));
        using var ecdh = ECDiffieHellman.Create();
        ecdh.ImportPkcs8PrivateKey(Convert.FromBase64String(body), out _);
        return Convert.ToBase64String(ecdh.ExportSubjectPublicKeyInfo());
    }

    // ── the sign-in sends a device key ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_exchange_is_sent_a_valid_P256_public_key()
    {
        OnGitHub();
        var keyless = Keyless();

        await Run(keyless, Settings());

        var sent = Assert.Single(keyless.PublicKeys);
        Assert.False(string.IsNullOrEmpty(sent));
        DeviceKeypair.Validate(sent!); // the SAME three checks the service applies
    }

    [Fact]
    public async Task Two_sign_ins_send_different_public_keys()
    {
        OnGitHub();
        var keyless = Keyless();

        await Run(keyless, Settings());
        await Run(keyless, Settings());

        Assert.Equal(2, keyless.PublicKeys.Count);
        Assert.NotEqual(keyless.PublicKeys[0], keyless.PublicKeys[1]);
    }

    // ── GitHub: mask first, then write ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task On_GitHub_the_api_key_and_every_PEM_line_are_masked_BEFORE_the_export_file_is_written()
    {
        OnGitHub();
        var keyless = Keyless();

        var (exit, stdout, _) = await Run(keyless, Settings());

        Assert.Equal(0, exit);
        var exported = ReadExport(_githubEnv);
        var masks = stdout.Lines.Where(l => l.Line.StartsWith("::add-mask::", StringComparison.Ordinal)).ToList();

        Assert.Contains(masks, m => m.Line == $"::add-mask::{Api}");
        foreach (var pemLine in exported.Pem)
            Assert.Contains(masks, m => m.Line == $"::add-mask::{pemLine}");

        // The ORDER is the control: GitHub masks only what it has already been told to mask when the file is read.
        Assert.All(masks, m => Assert.Equal(0, m.ExportFileLength));
        Assert.True(new FileInfo(_githubEnv).Length > 0);
    }

    [Fact]
    public async Task The_export_file_holds_the_api_key_and_a_PEM_whose_public_half_is_the_one_that_was_sent()
    {
        OnGitHub();
        var keyless = Keyless();

        await Run(keyless, Settings());

        var exported = ReadExport(_githubEnv);
        Assert.Equal($"BELLA_BAXTER_API_KEY={Api}", exported.ApiKeyLine);
        Assert.Equal($"BELLA_API_KEY={Api}", exported.LegacyLine);
        Assert.Matches(new Regex("^BELLA_EOF_[0-9a-f]{16,}$"), exported.Delimiter);
        Assert.Equal("-----BEGIN PRIVATE KEY-----", exported.Pem.First());
        Assert.Equal("-----END PRIVATE KEY-----", exported.Pem.Last());
        Assert.Equal(keyless.PublicKeys.Single(), PublicHalfOf(exported.Pem));
    }

    [Fact]
    public async Task The_heredoc_delimiter_is_random_per_sign_in()
    {
        OnGitHub();
        var keyless = Keyless();

        await Run(keyless, Settings());
        var first = ReadExport(_githubEnv).Delimiter;
        File.Delete(_githubEnv);
        await Run(keyless, Settings());
        var second = ReadExport(_githubEnv).Delimiter;

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task On_GitHub_the_private_key_is_not_printed_in_clear()
    {
        OnGitHub();

        var (_, stdout, _) = await Run(Keyless(), Settings());

        var exported = ReadExport(_githubEnv);
        var printedInClear = stdout.Lines
            .Where(l => !l.Line.StartsWith("::add-mask::", StringComparison.Ordinal))
            .Select(l => l.Line);
        Assert.DoesNotContain(printedInClear, l => exported.Pem.Any(p => p.Length > 20 && l.Contains(p, StringComparison.Ordinal)));
    }

    // ── elsewhere: never printed ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GitLab_output_never_contains_the_private_key_and_points_at_bella_run()
    {
        OnGitLab();

        var (exit, stdout, output) = await Run(Keyless(), Settings());

        Assert.Equal(0, exit);
        var printed = stdout.ToString();
        Assert.Contains($"export BELLA_BAXTER_API_KEY={Api}", printed);
        AssertNoPrivateKey(printed);
        Assert.Contains("bella run", string.Join("\n", output.Infos.Concat(output.Successes)) + printed);
    }

    [Fact]
    public async Task Generic_output_never_contains_the_private_key_and_points_at_bella_run()
    {
        var (exit, stdout, output) = await Run(Keyless(), Settings());

        Assert.Equal(0, exit);
        var printed = stdout.ToString();
        Assert.Contains($"BELLA_BAXTER_API_KEY={Api}", printed);
        AssertNoPrivateKey(printed);
        Assert.Contains("bella run", string.Join("\n", output.Infos.Concat(output.Successes)) + printed);
    }

    private static void AssertNoPrivateKey(string text)
    {
        Assert.DoesNotContain("PRIVATE KEY", text, StringComparison.Ordinal);
        // A P-256 PKCS#8 key is 138 bytes: 184 base64 characters on one line, or 64-character PEM lines.
        Assert.DoesNotMatch(new Regex("[A-Za-z0-9+/=]{60,}"), text.Replace(Api, string.Empty));
    }

    // ── --json ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Json_output_has_the_same_three_fields_and_no_private_key()
    {
        OnGitHub();

        var (exit, _, output) = await Run(Keyless(), Settings(json: true));

        Assert.Equal(0, exit);
        var written = Assert.Single(output.Objects);
        var json = JsonSerializer.Serialize(written);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(
            ["apiKey", "expiresAt", "platform"],
            doc.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.DoesNotContain("PRIVATE KEY", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private", json, StringComparison.OrdinalIgnoreCase);
        // The export file still carries the key for the next step; the object a caller parses never does.
        Assert.Contains("BELLA_BAXTER_PRIVATE_KEY<<", File.ReadAllText(_githubEnv), StringComparison.Ordinal);
    }

    // ── a refusal is named ──────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("device_key_required", 403)]
    [InlineData("device_key_malformed", 400)]
    public async Task The_servers_refusal_is_printed_by_name_and_nothing_is_exported(string code, int status)
    {
        OnGitHub();
        var keyless = Keyless();
        keyless.ExchangeThrows = new OidcExchangeRefusedException(new OidcExchangeRefusal(status, code, "server detail"));

        var (exit, _, output) = await Run(keyless, Settings());

        Assert.Equal(1, exit);
        var error = Assert.Single(output.Errors);
        Assert.Equal(code, error.Code);
        Assert.Contains(code, error.Message);
        Assert.DoesNotContain("TrustDomain is configured", error.Message); // not the generic text
        Assert.False(File.Exists(_githubEnv));
    }

    [Fact]
    public async Task A_refusal_with_no_reason_keeps_the_generic_trust_domain_message()
    {
        var keyless = Keyless();
        keyless.IssuedKey = null;

        var (exit, _, output) = await Run(keyless, Settings());

        Assert.Equal(1, exit);
        Assert.Equal("exchange_failed", Assert.Single(output.Errors).Code);
    }

    /// <summary>Records everything, including the objects a --json run writes.</summary>
    private sealed class CapturingOutput : IOutputWriter
    {
        public List<(string Message, string? Code)> Errors { get; } = [];
        public List<string> Infos { get; } = [];
        public List<string> Successes { get; } = [];
        public List<object?> Objects { get; } = [];

        public Task StatusAsync(string message, Func<Task> work) => work();
        public Task StatusAsync(string message, Func<Action<string>, Task> work) => work(_ => { });
        public void WriteObject<T>(T obj) => Objects.Add(obj);
        public void WriteList<T>(IEnumerable<T> items) { }
        public void WriteTable(string[] headers, IEnumerable<string[]> rows) { }
        public void WriteSuccess(string message) => Successes.Add(message);
        public void WriteError(string message, string? code = null) => Errors.Add((message, code));
        public void WriteWarning(string message) { }
        public void WriteInfo(string message) => Infos.Add(message);
    }
}
