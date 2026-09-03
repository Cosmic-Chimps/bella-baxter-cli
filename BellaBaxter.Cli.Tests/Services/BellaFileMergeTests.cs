using BellaCli.Services;
using static BellaCli.Services.KeyContextService;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// An API-key login writes the key's context into <c>.bella</c>. It must MERGE: the file also
/// carries <c>url</c> (which deployment the context belongs to — see <see cref="ApiUrlResolutionTests"/>),
/// and the overwrite this replaces dropped it on every login, so the next command silently went to
/// the machine-default server rather than the one the key had just been validated against.
/// </summary>
public class BellaFileMergeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bella-merge-" + Guid.NewGuid().ToString("N"));
    private string Bella => Path.Combine(_dir, ".bella");

    public BellaFileMergeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static KeyContext Ctx(string project = "spiffe-load-service", string? env = "local", string? org = "test-91798c") =>
        new(project, project, env, env, "CONSUMER", OrgSlug: org);

    [Fact]
    public void api_key_login_keeps_the_url_already_recorded_in_the_file()
    {
        File.WriteAllText(Bella, "project = \"old\"\nenvironment = \"dev\"\nurl = \"https://bb-lab-gw.cosmic-chimps.com\"\n");

        WriteBellaFile(_dir, Ctx(), apiUrl: "https://localhost:7500");

        var lines = File.ReadAllLines(Bella);
        Assert.Contains("url = \"https://bb-lab-gw.cosmic-chimps.com\"", lines);
        Assert.DoesNotContain("url = \"https://localhost:7500\"", lines);
        Assert.Contains("project = \"spiffe-load-service\"", lines);
        Assert.Contains("environment = \"local\"", lines);
        Assert.DoesNotContain("project = \"old\"", lines);
    }

    [Fact]
    public void a_new_file_records_the_server_the_key_was_validated_against()
    {
        WriteBellaFile(_dir, Ctx(), apiUrl: "https://localhost:7500");

        Assert.Equal(
            ["org = \"test-91798c\"", "project = \"spiffe-load-service\"", "environment = \"local\"", "url = \"https://localhost:7500\""],
            File.ReadAllLines(Bella));
    }

    [Fact]
    public void comments_and_unknown_keys_survive_in_place()
    {
        File.WriteAllText(Bella, "# team default\nproject = \"old\"\nenvironment = \"dev\"\nsomething_new = \"kept\"\nurl = \"https://x\"\n");

        WriteBellaFile(_dir, Ctx(org: null));

        Assert.Equal(
            ["# team default", "project = \"spiffe-load-service\"", "environment = \"local\"", "something_new = \"kept\"", "url = \"https://x\""],
            File.ReadAllLines(Bella));
    }

    [Fact]
    public void a_project_scoped_key_removes_a_stale_environment_line()
    {
        File.WriteAllText(Bella, "project = \"old\"\nenvironment = \"dev\"\nurl = \"https://x\"\n");

        WriteBellaFile(_dir, Ctx(env: null, org: null));

        Assert.Equal(["project = \"spiffe-load-service\"", "url = \"https://x\""], File.ReadAllLines(Bella));
    }

    [Fact]
    public void whoami_prefix_matches_the_consoles_key_prefix()
    {
        // The server computes KeyPrefix as $"bax-{id:N}"[..12] — "bax-" + 8 hex chars.
        var key = new StoredApiKey("c48a534d4d1f4c1b8bb14ba027fd4bba", "secret", "bax-c48a534d4d1f4c1b8bb14ba027fd4bba-secret");
        Assert.Equal("bax-c48a534d", key.KeyPrefix);
    }
}
