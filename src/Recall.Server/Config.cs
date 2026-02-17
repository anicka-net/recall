using System.Text.Json;

namespace Recall.Server;

public class RecallConfig
{
    public string DatabasePath { get; init; } = "";
    public string ModelPath { get; init; } = "";
    public string SystemPrompt { get; init; } = "";
    public int AutoContextLimit { get; init; } = 5;
    public int SearchResultLimit { get; init; } = 10;
    public string? OAuthPassphraseHash { get; init; }
    public string? OAuthBaseUrl { get; init; }
    public string? RestrictionSecretHash { get; init; }

    public static RecallConfig Load()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configDir = Path.Combine(home, ".recall");
        Directory.CreateDirectory(configDir);

        var configPath = Path.Combine(configDir, "config.json");
        ConfigFile? file = null;

        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                file = JsonSerializer.Deserialize<ConfigFile>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch { /* silently ignore malformed config */ }
        }

        // Load system prompt from file if specified
        var systemPrompt = file?.SystemPrompt ?? "";
        if (!string.IsNullOrEmpty(file?.PromptFile))
        {
            var promptPath = file.PromptFile.StartsWith('~')
                ? Path.Combine(home, file.PromptFile[2..])
                : file.PromptFile;
            if (File.Exists(promptPath))
            {
                try { systemPrompt = File.ReadAllText(promptPath).Trim(); }
                catch { /* fall back to inline prompt */ }
            }
        }

        var dbPath = file?.DatabasePath
            ?? Path.Combine(configDir, "recall.db");

        var modelPath = file?.ModelPath
            ?? Path.Combine(configDir, "models", "all-MiniLM-L6-v2");

        return new RecallConfig
        {
            DatabasePath = dbPath,
            ModelPath = modelPath,
            SystemPrompt = systemPrompt,
            AutoContextLimit = file?.AutoContextLimit ?? 5,
            SearchResultLimit = file?.SearchResultLimit ?? 10,
            OAuthPassphraseHash = file?.OAuthPassphraseHash,
            OAuthBaseUrl = file?.OAuthBaseUrl,
            RestrictionSecretHash = file?.RestrictionSecretHash,
        };
    }
}

public class ConfigFile
{
    public string? DatabasePath { get; set; }
    public string? ModelPath { get; set; }
    public string? SystemPrompt { get; set; }
    public string? PromptFile { get; set; }
    public int? AutoContextLimit { get; set; }
    public int? SearchResultLimit { get; set; }
    public string? OAuthPassphraseHash { get; set; }
    public string? OAuthBaseUrl { get; set; }
    public string? RestrictionSecretHash { get; set; }
}
