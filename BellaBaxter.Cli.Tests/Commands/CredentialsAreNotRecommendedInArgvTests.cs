using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;
using BellaCli.Commands;
using BellaCli.Commands.Totp;
using Spectre.Console.Cli;

namespace BellaBaxter.Cli.Tests.Commands;

/// <summary>
/// Issue #833 — a credential must not be the thing we TELL people to put in argv.
///
/// <para>#743 settled this for a secret's value and stopped there. Three siblings were left: the
/// login key, the scoped token, and the TOTP seed. An argument is visible in <c>ps</c> for the life
/// of the process, lands in shell history, and is echoed by any CI runner that logs the command it
/// ran — and unlike a secret's value, an API key does not expire, so neither does the exposure.</para>
///
/// <para><b>Why the assertions are about the RECOMMENDATION and not about the flag.</b> The flags
/// still exist; removing them would break scripts that already work. What changed is what the
/// product teaches, and that is exactly the kind of thing that is corrected once and drifts back in
/// the next error message somebody writes.</para>
/// </summary>
public class CredentialsAreNotRecommendedInArgvTests
{
    [Fact]
    public void The_totp_seed_has_a_path_that_is_not_an_argument()
    {
        // The otpauth:// URL carries the seed, so it needed the same two doors `secrets set` has.
        var options = typeof(ImportTotpSettings)
            .GetProperties()
            .SelectMany(p => p.GetCustomAttributes<CommandOptionAttribute>())
            .SelectMany(a => a.LongNames)
            .ToList();

        Assert.Contains("stdin", options);
        Assert.Contains("from-file", options);
    }

    [Fact]
    public void The_totp_url_argument_is_optional_so_the_other_paths_are_reachable()
    {
        // A REQUIRED positional would make --stdin unusable: Spectre refuses the command before the
        // handler runs, so the safe path would exist and be impossible to take.
        var argument = typeof(ImportTotpSettings)
            .GetProperty(nameof(ImportTotpSettings.OtpauthUrl))!
            .GetCustomAttribute<CommandArgumentAttribute>();

        Assert.NotNull(argument);
        Assert.False(argument!.IsRequired, "the otpauth-url argument must be optional — see the test body");
    }

    [Fact]
    public void The_inline_credential_options_say_what_they_cost()
    {
        // The description is where an operator reading --help learns the trade. Both of these are
        // kept for compatibility, so the help text is the only thing standing between the flag and
        // somebody choosing it by default.
        foreach (var (type, property) in new[]
                 {
                     (typeof(LoginSettings), nameof(LoginSettings.ApiKey)),
                     (typeof(ImportTotpSettings), nameof(ImportTotpSettings.OtpauthUrl)),
                 })
        {
            var description = type.GetProperty(property)!
                .GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";

            Assert.True(
                description.Contains("shell history", StringComparison.OrdinalIgnoreCase)
                    || description.Contains("ps", StringComparison.Ordinal),
                $"{type.Name}.{property} does not say why passing it inline costs something.");
        }
    }

    [Fact]
    public void No_cli_message_recommends_passing_the_key_as_an_argument()
    {
        // The pattern, not the call sites: five messages recommended `bella login --api-key bax-…`
        // and deleting those five protects only those five. A COMMENT explaining the mechanism is
        // fine — the flag is real — so this looks only at string literals.
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(CliSourceRoot(), "*.cs", SearchOption.AllDirectories))
        {
            foreach (var (line, number) in File.ReadLines(file).Select((l, i) => (l, i + 1)))
            {
                var code = line.TrimStart();
                if (code.StartsWith("//") || code.StartsWith("///") || code.StartsWith("*"))
                    continue;

                // A literal that hands the operator a ready-to-run command with a key in it.
                if (Regex.IsMatch(line, @"""[^""]*login --api-key\s+(bax-|bb_|\$|<)"))
                    offenders.Add($"{Path.GetFileName(file)}:{number}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These messages tell an operator to put the API key in argv, which puts it in `ps` and in "
                + "shell history — and an API key does not expire. Recommend plain `bella login` (it "
                + "prompts without echoing) or BELLA_BAXTER_API_KEY: "
                + string.Join(", ", offenders.Order()));
    }

    private static string CliSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "BellaBaxter.Cli")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "BellaBaxter.Cli");
    }
}
