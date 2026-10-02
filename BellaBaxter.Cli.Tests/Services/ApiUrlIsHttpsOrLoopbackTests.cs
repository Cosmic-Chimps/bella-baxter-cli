using System.Text.RegularExpressions;
using BellaCli.Commands.Mcp;
using BellaCli.Services;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// Backlog §2.31 follow-up — EVERY command that talks to the server, not only
/// <c>bella spiffe agent</c>, refuses a plain-http API address that is not this machine.
/// </summary>
/// <remarks>
/// <para>Parity over surfaces: an https-only rule on the SPIFFE agent and none on <c>bella login</c>,
/// <c>bella secrets get</c> or <c>bella mcp</c> is the asymmetry this repository keeps removing, and
/// the other commands carry more (the login tokens, API keys, decrypted secrets). The rule is applied
/// where every command gets its address, <see cref="ConfigService.ApiUrl"/>, and each refusal names
/// the SOURCE the address came from, because that is what the operator has to change.</para>
/// <para>These tests mutate process-wide state (environment variables, the working directory), so they
/// share a non-parallel collection with <see cref="ApiUrlResolutionTests"/>.</para>
/// </remarks>
[Collection(ApiUrlEnvironmentCollection.Name)]
public class ApiUrlIsHttpsOrLoopbackTests : IDisposable
{
    private readonly string _dir;
    private readonly string _originalCwd;
    private readonly string? _originalEnv;
    private readonly string? _originalDeprecated;

    public ApiUrlIsHttpsOrLoopbackTests()
    {
        _originalCwd = Directory.GetCurrentDirectory();
        _originalEnv = Environment.GetEnvironmentVariable("BELLA_BAXTER_URL");
        _originalDeprecated = Environment.GetEnvironmentVariable("BAXTER_URL");
        _dir = Path.Combine(Path.GetTempPath(), "bella-https-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Directory.SetCurrentDirectory(_dir);
        Environment.SetEnvironmentVariable("BELLA_BAXTER_URL", null);
        Environment.SetEnvironmentVariable("BAXTER_URL", null);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalCwd);
        Environment.SetEnvironmentVariable("BELLA_BAXTER_URL", _originalEnv);
        Environment.SetEnvironmentVariable("BAXTER_URL", _originalDeprecated);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData("BELLA_BAXTER_URL", "from BELLA_BAXTER_URL")]
    [InlineData("BAXTER_URL", "from BAXTER_URL (deprecated)")]
    public void An_environment_variable_naming_plain_http_off_this_machine_is_refused_and_named(string variable, string source)
    {
        Environment.SetEnvironmentVariable(variable, "http://bella.lan:5522");

        var ex = Assert.Throws<InsecureApiAddressException>(() => _ = new ConfigService().ApiUrl);

        Assert.Contains(source, ex.Message, StringComparison.Ordinal);
        Assert.Contains("must use https", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bella_file_naming_plain_http_off_this_machine_is_refused_and_named()
    {
        File.WriteAllText(Path.Combine(_dir, ".bella"), "project = \"p\"\nenvironment = \"e\"\nurl = \"http://10.0.0.5:5522\"\n");

        var ex = Assert.Throws<InsecureApiAddressException>(() => _ = new ConfigService().ApiUrl);

        Assert.Contains("from .bella", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://localhost:5522")]
    [InlineData("http://127.0.0.1:5522")]
    [InlineData("http://[::1]:5522")]
    [InlineData("https://api.bella-baxter.io")]
    [InlineData("https://192.168.1.20")]
    public void Https_and_loopback_http_are_accepted(string address)
    {
        // Loopback stays allowed, which is what keeps the dev API (and any test's local http server) usable.
        Environment.SetEnvironmentVariable("BELLA_BAXTER_URL", address);

        Assert.Equal(address, new ConfigService().ApiUrl);
    }

    [Fact]
    public void The_refused_value_can_still_be_DISPLAYED_so_the_operator_can_see_what_to_fix()
    {
        Environment.SetEnvironmentVariable("BELLA_BAXTER_URL", "http://bella.lan");
        var config = new ConfigService();

        Assert.Equal("http://bella.lan", config.ApiUrlAsConfigured);
        Assert.NotNull(config.ApiUrlProblem);
    }

    [Fact]
    public void The_mcp_flag_is_held_to_the_same_rule_and_names_itself()
    {
        var ex = Assert.Throws<InsecureApiAddressException>(
            () => McpCommand.ResolveApiBase("http://bella.lan/", new ConfigService()));

        Assert.Contains("from --api-url", ex.Message, StringComparison.Ordinal);
        Assert.Equal("http://localhost:5522", McpCommand.ResolveApiBase("http://localhost:5522/", new ConfigService()));
    }

    [Fact]
    public void Config_set_server_refuses_to_SAVE_an_address_every_later_command_would_refuse()
    {
        var ex = Assert.Throws<InsecureApiAddressException>(() => new ConfigService().SetApiUrl("http://bella.lan"));
        Assert.Contains("bella config set-server", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// ONE reader of the address. A command reading <c>BELLA_BAXTER_URL</c> itself bypasses the rule
    /// (Exec and SdkRun did, as a dead fallback). Empty allow-list.
    /// </summary>
    [Fact]
    public void Only_ConfigService_reads_the_address_from_the_environment()
    {
        var root = CliRoot();
        var reads = new Regex(@"GetEnvironmentVariable\(\s*""(BELLA_BAXTER_URL|BAXTER_URL|BELLA_API_URL)""", RegexOptions.CultureInvariant);

        var files = SourceFiles(root);
        Assert.True(files.Count > 100, $"read only {files.Count} files");

        var offenders = files
            .Where(f => Path.GetFileName(f) != "ConfigService.cs" && reads.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Reads the API address outside ConfigService, bypassing the https rule:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The unchecked accessor is for DISPLAY: no file that builds an HTTP request may read it. Empty allow-list.
    /// </summary>
    [Fact]
    public void The_unchecked_address_never_reaches_a_file_that_makes_requests()
    {
        var root = CliRoot();
        var http = new Regex(@"\bHttpClient\b|\bBaseAddress\b|new Uri\(|HttpRequestMessage|RequestAdapter", RegexOptions.CultureInvariant);

        var offenders = SourceFiles(root)
            .Where(f => Path.GetFileName(f) != "ConfigService.cs")
            .Where(f => File.ReadAllText(f) is var s && s.Contains("ApiUrlAsConfigured", StringComparison.Ordinal) && http.IsMatch(s))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();

        Assert.True(offenders.Count == 0,
            "ApiUrlAsConfigured (unchecked) is read in a file that sends requests; use ApiUrl:\n  " + string.Join("\n  ", offenders));
    }

    private static List<string> SourceFiles(string root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => Path.GetRelativePath(root, f).Replace('\\', '/').Split('/') is var parts
                && !parts.Contains("bin") && !parts.Contains("obj"))
            .ToList();

    private static string CliRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "BellaBaxter.Cli")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "BellaBaxter.Cli");
    }
}

/// <summary>Tests that mutate BELLA_BAXTER_URL / the working directory run alone, never beside others.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ApiUrlEnvironmentCollection
{
    public const string Name = "api-url-environment";
}
