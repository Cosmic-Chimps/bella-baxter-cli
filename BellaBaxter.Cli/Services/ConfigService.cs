using System.Text.Json;

namespace BellaCli.Services;

public record BellaConfig(
    string ApiUrl = "https://api.bella-baxter.io"
);

public class ConfigService
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config",
        "bella-cli"
    );

    private static readonly string ConfigFile = Path.Combine(ConfigDir, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private BellaConfig _cache = new();

    public ConfigService()
    {
        Directory.CreateDirectory(ConfigDir);
        _cache = Load();
    }

    public BellaConfig Config => _cache;

    public string ApiUrl =>
        Environment.GetEnvironmentVariable("BELLA_BAXTER_URL")?.TrimEnd('/')
        ?? Environment.GetEnvironmentVariable("BAXTER_URL")?.TrimEnd('/')   // deprecated
        ?? _cache.ApiUrl;

    public void SetApiUrl(string url)
    {
        _cache = _cache with { ApiUrl = url };
        Save();
    }

    private BellaConfig Load()
    {
        if (!File.Exists(ConfigFile))
            return new BellaConfig();

        try
        {
            var json = File.ReadAllText(ConfigFile);
            return JsonSerializer.Deserialize<BellaConfig>(json, JsonOptions) ?? new BellaConfig();
        }
        catch
        {
            return new BellaConfig();
        }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_cache, JsonOptions);
        File.WriteAllText(ConfigFile, json);
    }
}
