using BellaCli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;

namespace BellaCli.Commands.Generate;

public class GenerateCommand(IOutputWriter output) : AsyncCommand<GenerateCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandOption("--memorable")]
        [Description("Generate a memorable passphrase instead of a random password")]
        public bool Memorable { get; set; }

        [CommandOption("--length <n>")]
        [Description("Password length (default: 20, range: 8-128)")]
        [DefaultValue(20)]
        public int Length { get; set; } = 20;

        [CommandOption("--words <n>")]
        [Description("Number of words for passphrase (default: 5, range: 3-8)")]
        [DefaultValue(5)]
        public int Words { get; set; } = 5;

        [CommandOption("--no-uppercase")]
        [Description("Exclude uppercase letters")]
        public bool NoUppercase { get; set; }

        [CommandOption("--no-numbers")]
        [Description("Exclude numbers")]
        public bool NoNumbers { get; set; }

        [CommandOption("--no-symbols")]
        [Description("Exclude symbols")]
        public bool NoSymbols { get; set; }

        [CommandOption("--separator <type>")]
        [Description("Passphrase separator: hyphens|spaces|periods|commas|underscores (default: hyphens)")]
        [DefaultValue("hyphens")]
        public string Separator { get; set; } = "hyphens";

        [CommandOption("--exclude-ambiguous")]
        [Description("Exclude ambiguous characters (0, O, 1, l, I)")]
        public bool ExcludeAmbiguous { get; set; }

        [CommandOption("--quiet|-q")]
        [Description("Output only the value (no labels, for piping to clipboard etc.)")]
        public bool Quiet { get; set; }
    }

    public override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        string result;

        if (settings.Memorable)
        {
            var wordCount = Math.Clamp(settings.Words, 3, 8);
            var sep = settings.Separator switch
            {
                "spaces" => " ",
                "periods" => ".",
                "commas" => ",",
                "underscores" => "_",
                _ => "-",
            };
            result = GeneratePassphrase(wordCount, sep);
        }
        else
        {
            var length = Math.Clamp(settings.Length, 8, 128);
            result = GeneratePassword(length, !settings.NoUppercase, !settings.NoNumbers, !settings.NoSymbols, settings.ExcludeAmbiguous);
        }

        if (settings.Quiet)
        {
            Console.Write(result);
            return Task.FromResult(0);
        }

        var strength = GetStrength(result);
        var (strengthColor, strengthLabel) = strength switch
        {
            "weak" => ("red", "Weak"),
            "fair" => ("yellow", "Fair"),
            "strong" => ("green", "Strong"),
            _ => ("brightgreen", "Very Strong"),
        };

        if (output is HumanOutputWriter)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"  [bold]{Markup.Escape(result)}[/]");
            AnsiConsole.MarkupLine($"  [{strengthColor}]{strengthLabel}[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[dim]  Tip: bella generate --quiet | pbcopy[/]");
            AnsiConsole.WriteLine();
        }
        else
        {
            output.WriteObject(new { value = result, strength = strengthLabel.ToLowerInvariant().Replace(" ", "-") });
        }

        return Task.FromResult(0);
    }

    private static string GeneratePassword(int length, bool uppercase, bool numbers, bool symbols, bool excludeAmbiguous)
    {
        var charsets = new List<string>();
        charsets.Add(excludeAmbiguous ? "bcdfghjkmnpqrstvwxyz" : "abcdefghijklmnopqrstuvwxyz");
        if (uppercase) charsets.Add(excludeAmbiguous ? "BCDFGHJKMNPQRSTVWXYZ" : "ABCDEFGHIJKLMNOPQRSTUVWXYZ");
        if (numbers) charsets.Add(excludeAmbiguous ? "23456789" : "0123456789");
        if (symbols) charsets.Add("!@#$%^&*()-_=+[]{}|;:,.<>?");

        var pool = string.Concat(charsets);
        var bytes = new byte[length * 4];
        RandomNumberGenerator.Fill(bytes);

        var sb = new StringBuilder(length);
        var idx = 0;
        while (sb.Length < length)
        {
            var pick = BitConverter.ToUInt32(bytes, idx * 4) % (uint)pool.Length;
            sb.Append(pool[(int)pick]);
            idx++;
            if (idx * 4 >= bytes.Length)
            {
                RandomNumberGenerator.Fill(bytes);
                idx = 0;
            }
        }
        return sb.ToString();
    }

    // Word list for passphrases (EFF short word list subset)
    private static readonly string[] WordList =
    [
        "apple","brave","cloud","dance","eagle","flame","grace","house","ivory","jewel",
        "kraft","lemon","maple","night","ocean","piano","queen","river","stone","tiger",
        "ultra","vivid","water","xenon","yacht","zebra","amber","blaze","crisp","delta",
        "ember","frost","globe","haven","input","joker","knack","lunar","magic","noble",
        "ozone","pixel","quest","radar","storm","trend","umbra","valor","wheat","yield",
        "abbot","baker","cedar","ditch","every","forge","gravel","hatch","irony","jungle",
        "kiosk","light","metal","nerve","optic","press","quill","rocky","sharp","toast",
        "under","vault","world","xray","young","zones","along","bench","cable","depot",
        "eight","ferry","groan","hotel","index","joint","kings","lodge","march","north",
        "order","plumb","quote","ranch","serve","thick","uncle","verge","witch","extra"
    ];

    private static string GeneratePassphrase(int wordCount, string separator)
    {
        var bytes = new byte[wordCount * 4];
        RandomNumberGenerator.Fill(bytes);
        var words = new string[wordCount];
        for (int i = 0; i < wordCount; i++)
        {
            var pick = BitConverter.ToUInt32(bytes, i * 4) % (uint)WordList.Length;
            var word = WordList[(int)pick];
            words[i] = char.ToUpper(word[0]) + word[1..];
        }
        // Append a random 2-digit number to last word for extra entropy
        var numByte = new byte[1];
        RandomNumberGenerator.Fill(numByte);
        words[^1] += (numByte[0] % 90 + 10).ToString();
        return string.Join(separator, words);
    }

    private static string GetStrength(string password)
    {
        var score = 0;
        if (password.Length >= 12) score++;
        if (password.Length >= 16) score++;
        if (password.Length >= 20) score++;
        if (password.Any(char.IsUpper)) score++;
        if (password.Any(char.IsDigit)) score++;
        if (password.Any(c => "!@#$%^&*()-_=+[]{}|;:,.<>?".Contains(c))) score++;
        return score switch { <= 2 => "weak", <= 3 => "fair", <= 4 => "strong", _ => "very-strong" };
    }
}
