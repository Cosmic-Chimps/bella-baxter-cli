using System.Collections;
using System.Text.RegularExpressions;

namespace BellaBaxter.Cli.Tests.Commands.Run;

/// <summary>
/// Issue #1049 — <c>bella run</c> builds the child's environment in ONE place, and a secret named for a variable that
/// controls how a process loads code is withheld with a warning rather than silently overriding the inherited value.
/// </summary>
/// <remarks>
/// <c>RunCommand</c> had two independent copies of the overlay — <c>SpawnChild</c> (watch mode) and
/// <c>SpawnProcess</c> — each inheriting the parent's environment and then writing every secret over it. A filter
/// added to one would have left the other open, so the overlay is collapsed into <c>RunEnvironment.BuildStartInfo</c>
/// first, and a source test keeps it single-author.
/// </remarks>
public class RunEnvironmentTests
{
    [Fact]
    public void RunCommand_has_one_author_of_the_child_environment()
    {
        var source = File.ReadAllText(Path.Combine(CliProjectDir(), "Commands", "Run", "RunCommand.cs"));

        // The old shape, twice: inherit the environment entry by entry, then overlay every secret.
        Assert.False(Regex.IsMatch(source, @"DictionaryEntry\s+entry\s+in"),
            "RunCommand inherits the environment itself; build the start info through RunEnvironment.BuildStartInfo");
        Assert.False(Regex.IsMatch(source, @"foreach \(var \(k, v\) in secrets\)"),
            "RunCommand overlays secrets itself; build the start info through RunEnvironment.BuildStartInfo");
        Assert.Equal(2, Regex.Matches(source, @"RunEnvironment\.BuildStartInfo\(").Count);
    }

    private static readonly Hashtable Inherited = new()
    {
        ["PATH"] = "/usr/bin:/bin",
        ["HOME"] = "/home/runner",
    };

    [Fact]
    public void A_reserved_secret_is_withheld_and_the_inherited_value_kept()
    {
        var secrets = new Dictionary<string, string>
        {
            ["PATH"] = "/tmp/evil:/usr/bin",
            ["LD_PRELOAD"] = "/tmp/evil.so",
            ["NODE_OPTIONS"] = "--require /tmp/x.js",
            ["DATABASE_URL"] = "postgres://db/app",
            ["NODE_ENV"] = "production",
        };

        var (psi, withheld) = BellaCli.Commands.Run.RunEnvironment.BuildStartInfo(["node", "app.js"], secrets, Inherited, allowReserved: false);

        Assert.Equal(["LD_PRELOAD", "NODE_OPTIONS", "PATH"], withheld);
        Assert.Equal("/usr/bin:/bin", psi.Environment["PATH"]);         // inherited, not the secret's
        Assert.False(psi.Environment.ContainsKey("LD_PRELOAD"));
        Assert.False(psi.Environment.ContainsKey("NODE_OPTIONS"));
        Assert.Equal("postgres://db/app", psi.Environment["DATABASE_URL"]); // ordinary names unaffected
        Assert.Equal("production", psi.Environment["NODE_ENV"]);
        Assert.Equal("node", psi.FileName);
        Assert.Equal(["app.js"], psi.ArgumentList);
    }

    [Fact]
    public void The_explicit_opt_in_injects_them()
    {
        var secrets = new Dictionary<string, string> { ["PATH"] = "/opt/tool/bin:/usr/bin" };

        var (psi, withheld) = BellaCli.Commands.Run.RunEnvironment.BuildStartInfo(["sh"], secrets, Inherited, allowReserved: true);

        Assert.Empty(withheld);
        Assert.Equal("/opt/tool/bin:/usr/bin", psi.Environment["PATH"]);
    }

    [Fact]
    public void Each_withheld_name_gets_a_warning_that_names_it_and_the_opt_in()
    {
        var warning = Assert.Single(BellaCli.Commands.Run.RunEnvironment.Warnings(["LD_PRELOAD"]));
        Assert.Contains("'LD_PRELOAD'", warning);
        Assert.Contains("dynamic loader", warning);
        Assert.Contains("--allow-reserved-env", warning);
    }

    [Fact]
    public void The_CLI_and_the_API_reserve_exactly_the_same_names()
    {
        // Two copies, one rule (#1049): the CLI builds in the public repository, where the API does not exist.
        var api = MonorepoFile("apps/api/baxter-dotnet/BellaBaxter.Api/Features/Secrets/Shared/ReservedEnvironmentNames.cs");
        Assert.SkipWhen(api is null, "not a monorepo checkout — the API is not present");

        var apiExact = Regex.Matches(api!, @"\[""([^""]+)""\] = ReservedEnvironmentNameClass\.(\w+)")
            .Select(m => $"{m.Groups[1].Value}={m.Groups[2].Value}").Order(StringComparer.Ordinal).ToArray();
        var apiPrefixes = Regex.Matches(api!, @"\(""([^""]+)"", ReservedEnvironmentNameClass\.(\w+)\)")
            .Select(m => $"{m.Groups[1].Value}={m.Groups[2].Value}").Order(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(apiExact);
        Assert.NotEmpty(apiPrefixes);

        var cliExact = BellaCli.Services.ReservedEnvironmentNames.Exact
            .Select(kv => $"{kv.Key}={kv.Value}").Order(StringComparer.Ordinal).ToArray();
        var cliPrefixes = BellaCli.Services.ReservedEnvironmentNames.Prefixes
            .Select(p => $"{p.Prefix}={p.Class}").Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(apiExact, cliExact);
        Assert.Equal(apiPrefixes, cliPrefixes);
    }

    private static string? MonorepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "apps", "cli-dotnet")))
            dir = dir.Parent;
        if (dir is null) return null;
        var path = Path.Combine(dir.FullName, relative);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static string CliProjectDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BellaBaxter.Cli", "BellaBaxter.Cli.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "BellaBaxter.Cli");
    }
}
