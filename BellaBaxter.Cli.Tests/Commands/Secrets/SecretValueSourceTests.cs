using BellaCli.Commands.Secrets;

namespace BellaBaxter.Cli.Tests.Commands.Secrets;

/// <summary>
/// Issue #743 — where a secret's value comes from.
///
/// <para>The selection rule is pure so it can be pinned without a console or a file system. What
/// matters here is not that each flag works, but that a combination is REFUSED rather than resolved
/// by precedence: silently ignoring one of two supplied values looks exactly like success, and the
/// ignored one could be the correct one.</para>
/// </summary>
public class SecretValueSourceTests
{
    [Fact]
    public void A_positional_value_is_used_when_it_is_the_only_one()
    {
        Assert.True(SecretValueSource.TrySelect("v", stdin: false, fromFile: null, out var kind, out _));
        Assert.Equal(SecretValueSource.Kind.Positional, kind);
    }

    [Fact]
    public void Stdin_is_used_when_asked_for()
    {
        Assert.True(SecretValueSource.TrySelect(null, stdin: true, fromFile: null, out var kind, out _));
        Assert.Equal(SecretValueSource.Kind.Stdin, kind);
    }

    [Fact]
    public void A_file_is_used_when_named()
    {
        Assert.True(SecretValueSource.TrySelect(null, stdin: false, fromFile: "sa.json", out var kind, out _));
        Assert.Equal(SecretValueSource.Kind.File, kind);
    }

    [Fact]
    public void Nothing_given_falls_through_to_the_prompt()
    {
        Assert.True(SecretValueSource.TrySelect(null, stdin: false, fromFile: null, out var kind, out _));
        Assert.Equal(SecretValueSource.Kind.Prompt, kind);
    }

    [Theory]
    [InlineData("v", true, null)]
    [InlineData("v", false, "sa.json")]
    [InlineData(null, true, "sa.json")]
    [InlineData("v", true, "sa.json")]
    public void Two_sources_are_refused_rather_than_ranked(string? positional, bool stdin, string? file)
    {
        Assert.False(SecretValueSource.TrySelect(positional, stdin, file, out _, out var error));
        Assert.NotNull(error);
        // The message must name what was actually supplied — "invalid arguments" leaves the operator
        // guessing which of three flags to drop.
        Assert.Contains("--stdin", error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_positional_value_is_not_a_source()
    {
        // `bella secrets set KEY "" --stdin` is piping into an explicit empty argument, not a clash.
        Assert.True(SecretValueSource.TrySelect("", stdin: true, fromFile: null, out var kind, out _));
        Assert.Equal(SecretValueSource.Kind.Stdin, kind);
    }

    [Fact]
    public void A_trailing_newline_is_part_of_the_value()
    {
        // A PEM ends with one. Trimming would mean --from-file stored something that is not the file.
        Assert.Equal("value\n", SecretValueSource.Decode("value\n"u8.ToArray()));
    }

    [Fact]
    public void Inner_newlines_and_whitespace_survive()
    {
        const string json = "{\n  \"type\": \"service_account\"\n}\n";
        Assert.Equal(json, SecretValueSource.Decode(System.Text.Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void A_utf8_bom_is_stripped()
    {
        // A BOM is an encoding marker, not content: kept, it silently breaks every consumer that
        // parses the secret as JSON — which is the case this feature exists for.
        byte[] withBom = [0xEF, 0xBB, 0xBF, .. "{}"u8.ToArray()];
        Assert.Equal("{}", SecretValueSource.Decode(withBom));
    }

    [Fact]
    public void Non_ascii_content_round_trips()
    {
        Assert.Equal("contraseña–ü", SecretValueSource.Decode(System.Text.Encoding.UTF8.GetBytes("contraseña–ü")));
    }

    [Fact]
    public async Task A_file_is_read_byte_for_byte()
    {
        var path = Path.GetTempFileName();
        try
        {
            const string pem = "-----BEGIN PRIVATE KEY-----\nMIIE\n-----END PRIVATE KEY-----\n";
            await File.WriteAllTextAsync(path, pem);

            Assert.Equal(pem, await SecretValueSource.ReadFileAsync(path, CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
