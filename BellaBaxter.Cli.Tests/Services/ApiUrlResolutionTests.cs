using BellaCli.Services;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// Where the CLI's server URL comes from, most specific first: BELLA_BAXTER_URL → the deprecated
/// BAXTER_URL → the nearest <c>.bella</c>'s <c>url</c> → <c>config.json</c>.
///
/// <para>The <c>.bella</c> step is the point of this: a project+environment slug does NOT identify a
/// deployment — <c>nginx-rotation/dev</c> exists on the hosted service and on a self-hosted box alike
/// — so a context file naming only the project was ambiguous, and the ambiguity got settled by
/// whatever happened to be exported in that shell. That is why the URL had to be re-exported every
/// session. Recording it next to the context removes the export.</para>
///
/// <para>An explicit environment variable still wins, so CI and one-off retargeting are unaffected.</para>
/// </summary>
public class ApiUrlResolutionTests : IDisposable
{
    private readonly string _dir;
    private readonly string _originalCwd;
    private readonly string? _originalEnv;
    private readonly string? _originalDeprecated;

    public ApiUrlResolutionTests()
    {
        _originalCwd = Directory.GetCurrentDirectory();
        _originalEnv = Environment.GetEnvironmentVariable("BELLA_BAXTER_URL");
        _originalDeprecated = Environment.GetEnvironmentVariable("BAXTER_URL");

        _dir = Path.Combine(Path.GetTempPath(), "bella-url-" + Guid.NewGuid().ToString("N"));
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

    private static void WriteBella(string dir, string? url)
    {
        var body = "project = \"nginx-rotation\"\nenvironment = \"dev\"\n";
        if (url is not null)
            body += $"url = \"{url}\"\n";
        File.WriteAllText(Path.Combine(dir, ".bella"), body);
    }

    [Fact]
    public void the_directory_context_supplies_the_server_with_no_environment_variable()
    {
        WriteBella(_dir, "https://bb-lab-gw.cosmic-chimps.com");

        var config = new ConfigService();

        Assert.Equal("https://bb-lab-gw.cosmic-chimps.com", config.ApiUrl);
        Assert.Equal(".bella", config.ApiUrlSource);
    }

    [Fact]
    public void an_explicit_environment_variable_still_wins()
    {
        // One-off retargeting and CI must keep working — the directory must not trap a session.
        WriteBella(_dir, "https://bb-lab-gw.cosmic-chimps.com");
        Environment.SetEnvironmentVariable("BELLA_BAXTER_URL", "https://other.example.com");

        var config = new ConfigService();

        Assert.Equal("https://other.example.com", config.ApiUrl);
        Assert.Equal("BELLA_BAXTER_URL", config.ApiUrlSource);
    }

    [Fact]
    public void a_context_without_a_url_falls_back_to_the_machine_default()
    {
        // Every .bella written before this feature has no url line; those must keep working.
        WriteBella(_dir, url: null);

        var config = new ConfigService();

        Assert.Equal(BellaConfig.DefaultApiUrl, config.ApiUrl);
        Assert.Equal("config.json", config.ApiUrlSource);
    }

    [Fact]
    public void the_url_is_found_from_a_subdirectory()
    {
        // Context is directory-scoped by walking UP, so it must work from anywhere in the tree.
        WriteBella(_dir, "https://bb-lab-gw.cosmic-chimps.com");
        var nested = Path.Combine(_dir, "src", "deep");
        Directory.CreateDirectory(nested);
        Directory.SetCurrentDirectory(nested);

        Assert.Equal("https://bb-lab-gw.cosmic-chimps.com", new ConfigService().ApiUrl);
    }

    [Theory]
    // A malformed or hostile url line must degrade to the default, never throw: this property is read
    // by every command, so an exception here would break the whole CLI in that directory.
    [InlineData("url =")]
    [InlineData("url")]
    [InlineData("url = \"\"")]
    public void a_malformed_url_line_degrades_instead_of_throwing(string line)
    {
        File.WriteAllText(
            Path.Combine(_dir, ".bella"),
            $"project = \"p\"\nenvironment = \"e\"\n{line}\n");

        var config = new ConfigService();

        Assert.Equal(BellaConfig.DefaultApiUrl, config.ApiUrl);
    }

    [Fact]
    public void a_trailing_slash_is_trimmed_so_request_paths_do_not_double_up()
    {
        WriteBella(_dir, "https://bb-lab-gw.cosmic-chimps.com/");

        Assert.Equal("https://bb-lab-gw.cosmic-chimps.com", new ConfigService().ApiUrl);
    }
}
