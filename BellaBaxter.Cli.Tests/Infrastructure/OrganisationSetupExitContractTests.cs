using System.Text.Json;
using BellaBaxter.Cli.Tests.Helpers;
using BellaBaxter.Cli.Tests.Services;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace BellaBaxter.Cli.Tests.Infrastructure;

/// <summary>
/// Spec 108 (FR-018, SC-008) — a person with no organisation who runs any command is told so on STDERR with exit 1, and
/// stdout stays empty. Runs the REAL built <c>bella</c> as a child process against a loopback server that answers every
/// request 403 <c>organisation-setup-required</c>, with a stored login the child reads from its own isolated HOME: the
/// in-process tests prove the exception and the handler, only this proves the process.
/// </summary>
[Collection(ApiUrlEnvironmentCollection.Name)]
public class OrganisationSetupExitContractTests
{
    private const string Sentence = "Finish setting up your organisation in the Bella console.";
    private const string SetupUrl = "https://console.example.test/onboarding";

    private static readonly string Refusal =
        $$"""{"type":"organisation-setup-required","title":"The organisation is not set up.","status":403,"detail":"{{Sentence}}","setupUrl":"{{SetupUrl}}"}""";

    /// <summary>
    /// Writes <c>tokens.json</c> into <paramref name="home"/> exactly as <see cref="CredentialStore"/> does (an encrypted
    /// blob under a key ring in the same directory), without touching the real user's profile: the store's paths are
    /// static and read once, so it cannot be pointed at a temporary home from inside the test process.
    /// </summary>
    private static void SeedStoredLogin(string home, string serverUrl)
    {
        var configDir = Path.Combine(home, ".config", "bella-cli");
        var keys = Path.Combine(configDir, "keys");
        Directory.CreateDirectory(keys);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(configDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(keys, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var services = new ServiceCollection();
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(keys))
            .SetApplicationName("bella-cli");
        var protector = services.BuildServiceProvider()
            .GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("bella-cli.credentials.v1");

        var tokens = new StoredTokens(
            AccessToken: "access-token-for-the-contract-test",
            RefreshToken: "refresh-token-for-the-contract-test",
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
            Origin: ConfigService.OriginOf(serverUrl));
        var json = JsonSerializer.Serialize(tokens, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        var file = Path.Combine(configDir, "tokens.json");
        File.WriteAllText(file, protector.Protect(json));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public async Task A_command_answered_organisation_setup_required_exits_1_with_the_sentence_and_link_on_stderr()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The child's credential directory rules are Unix-only here.");

        using var server = new FakeBellaServer().Always(403, Refusal);
        using var bella = new BellaProcess();
        SeedStoredLogin(bella.Home, server.Url);

        var (exit, stdout, stderr) = await bella.RunAsync(
            new Dictionary<string, string> { ["BELLA_BAXTER_URL"] = server.Url },
            ["secrets", "list", "-p", "sandbox", "-e", "dev"]);

        Assert.True(server.Requests.Count > 0, "the command never reached the server, so the stored login was not used: " + stderr);
        Assert.Equal(1, exit);
        var said = BellaProcess.Said(string.Empty, stderr);
        Assert.Contains(Sentence, said, StringComparison.Ordinal);
        Assert.Contains(SetupUrl, said, StringComparison.Ordinal);
        Assert.DoesNotContain(Sentence, stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(SetupUrl, stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("403", said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_human_error_goes_to_stderr_and_stdout_stays_empty()
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);

            new HumanOutputWriter().WriteError($"{Sentence} {SetupUrl}", "organisation_setup_required");
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        Assert.Equal(string.Empty, stdout.ToString());
        var written = stderr.ToString();
        Assert.Contains(Sentence, written, StringComparison.Ordinal);
        Assert.Contains(SetupUrl, written, StringComparison.Ordinal); // not wrapped away by a narrow console
        Assert.Contains("organisation_setup_required", written, StringComparison.Ordinal);
        Assert.Single(written.TrimEnd().Split('\n'));
    }

    [Fact]
    public void A_human_error_keeps_its_markup_escaped()
    {
        var originalError = Console.Error;
        var stderr = new StringWriter();
        try
        {
            Console.SetError(stderr);
            new HumanOutputWriter().WriteError("bad [red]markup[/] here");
        }
        finally
        {
            Console.SetError(originalError);
        }

        Assert.Contains("bad [red]markup[/] here", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Warnings_and_the_rest_stay_on_stdout()
    {
        // Only errors move (R15): a scripted caller that reads a human writer's stdout for a warning keeps working.
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            // AnsiConsole.Console is created lazily on the first use; give it the redirected writer.
            var original = Spectre.Console.AnsiConsole.Console;
            Spectre.Console.AnsiConsole.Console = Spectre.Console.AnsiConsole.Create(
                new Spectre.Console.AnsiConsoleSettings { Out = new Spectre.Console.AnsiConsoleOutput(stdout) });
            try
            {
                new HumanOutputWriter().WriteWarning("heads up");
            }
            finally
            {
                Spectre.Console.AnsiConsole.Console = original;
            }
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        Assert.Contains("heads up", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, stderr.ToString());
    }
}
