using System.Text;

namespace BellaCli.Commands.Secrets;

/// <summary>
/// Where a secret's value comes from — issue #743.
///
/// <para>Before this, the only way to write one secret was <c>bella secrets set KEY VALUE</c>. A
/// value passed as an argument is visible in <c>ps</c> for the life of the process, lands in shell
/// history, and is echoed by any CI runner that logs the command it ran. For a secrets manager that
/// is the wrong default, and it left multi-line values (a service-account JSON, a PEM) with no path
/// at all.</para>
///
/// <para>The selection is a PURE function so it can be tested without a console, a file system or a
/// network: the rule about which sources may be combined is the part that must not drift.</para>
/// </summary>
public static class SecretValueSource
{
    public enum Kind
    {
        /// <summary>The positional <c>[value]</c> argument.</summary>
        Positional,

        /// <summary>The whole of stdin.</summary>
        Stdin,

        /// <summary>The whole of a file.</summary>
        File,

        /// <summary>Nothing was given; ask the operator.</summary>
        Prompt,
    }

    /// <summary>
    /// Decide which source to read, or explain why the combination is refused.
    ///
    /// <para>More than one source is an ERROR rather than a precedence rule. Precedence would mean
    /// silently ignoring a value the caller supplied — and the one being ignored could be the
    /// correct one, which is indistinguishable from the command working.</para>
    /// </summary>
    public static bool TrySelect(
        string? positionalValue,
        bool stdin,
        string? fromFile,
        out Kind kind,
        out string? error
    )
    {
        var sources = new List<string>();
        if (!string.IsNullOrEmpty(positionalValue))
            sources.Add("a positional value");
        if (stdin)
            sources.Add("--stdin");
        if (!string.IsNullOrWhiteSpace(fromFile))
            sources.Add("--from-file");

        if (sources.Count > 1)
        {
            kind = Kind.Prompt;
            var listed =
                sources.Count == 2
                    ? $"{sources[0]} and {sources[1]}"
                    : $"{string.Join(", ", sources[..^1])} and {sources[^1]}";
            error =
                $"Give the value once: {listed} were all supplied. "
                + "Use --stdin for automation, --from-file for a file, or a positional value interactively.";
            return false;
        }

        error = null;
        kind = sources.Count == 0
            ? Kind.Prompt
            : sources[0] switch
            {
                "--stdin" => Kind.Stdin,
                "--from-file" => Kind.File,
                _ => Kind.Positional,
            };
        return true;
    }

    /// <summary>
    /// Decode a value read as bytes.
    ///
    /// <para><b>A trailing newline is kept.</b> It is content: a PEM file ends with one, and
    /// trimming would mean <c>--from-file cert.pem</c> stored something that is not the file. Callers
    /// who do not want one should pipe with <c>printf %s</c> rather than <c>echo</c>.</para>
    ///
    /// <para>A leading UTF-8 byte-order mark IS removed. A BOM is an encoding marker rather than part
    /// of the value, and keeping it silently breaks every consumer that parses the secret as JSON —
    /// which is the case this feature exists for.</para>
    /// </summary>
    public static string Decode(byte[] bytes)
    {
        ReadOnlySpan<byte> span = bytes;
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
            span = span[3..];

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(span);
    }

    /// <summary>Read the whole of stdin as bytes, so the decoding rule above is the only one.</summary>
    public static async Task<string> ReadStdinAsync(CancellationToken ct)
    {
        await using var input = Console.OpenStandardInput();
        using var buffer = new MemoryStream();
        await input.CopyToAsync(buffer, ct);
        return Decode(buffer.ToArray());
    }

    /// <summary>Read the whole of a file as bytes.</summary>
    public static async Task<string> ReadFileAsync(string path, CancellationToken ct) =>
        Decode(await File.ReadAllBytesAsync(path, ct));
}
