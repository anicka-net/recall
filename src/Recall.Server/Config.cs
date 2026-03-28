using System.Text.Json;
using Recall.Storage;

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
    public string? GuardianSecretHash { get; init; }
    public string? CodingSecretHash { get; init; }
    public List<McpProxyEntry> McpProxies { get; init; } = [];
    public List<ScopeEntry> Scopes { get; init; } = [];
    public int TierHotDays { get; init; } = 7;
    public int TierWarmDays { get; init; } = 90;
    public List<string> Tools { get; init; } = [];
    public MemoryFeatures Memory { get; init; } = new();
    public string? RohlikUsername { get; init; }
    public string? RohlikPassword { get; init; }
    public string? RohlikBaseUrl { get; init; }

    /// <summary>
    /// Resolve effective memory features for a scope (or guardian/coding).
    /// Scope overrides take precedence over global defaults.
    /// </summary>
    public MemoryFeatures GetMemoryFeatures(string? scopeName)
    {
        MemoryFeatures? scopeOverride = null;
        if (scopeName != null)
        {
            var scope = Scopes.FirstOrDefault(s =>
                string.Equals(s.Name, scopeName, StringComparison.OrdinalIgnoreCase));
            scopeOverride = scope?.Memory;
        }
        return MemoryFeatures.Resolve(scopeOverride, Memory);
    }

    /// <summary>
    /// Check if a tool module should be enabled.
    /// Empty tools list = all enabled (default). Otherwise only listed modules.
    /// Valid modules: diary, health, rohlik
    /// </summary>
    public bool IsToolEnabled(string module) =>
        Tools.Count == 0 || Tools.Contains(module, StringComparer.OrdinalIgnoreCase);

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
            GuardianSecretHash = file?.GuardianSecretHash,
            CodingSecretHash = file?.CodingSecretHash,
            McpProxies = file?.McpProxies ?? [],
            Scopes = file?.Scopes ?? [],
            TierHotDays = file?.TierHotDays ?? 7,
            TierWarmDays = file?.TierWarmDays ?? 90,
            Tools = file?.Tools ?? [],
            Memory = file?.Memory ?? new MemoryFeatures(),
            RohlikUsername = file?.RohlikUsername,
            RohlikPassword = file?.RohlikPassword,
            RohlikBaseUrl = file?.RohlikBaseUrl ?? "https://www.rohlik.cz",
        };
    }
}

public class McpProxyEntry
{
    public string Prefix { get; set; } = "";
    public string Target { get; set; } = "";
}

// ScopeEntry is defined in Recall.Storage (used by DiaryDatabase.ResolveAccess)

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
    public string? GuardianSecretHash { get; set; }
    public string? CodingSecretHash { get; set; }
    public List<McpProxyEntry>? McpProxies { get; set; }
    public List<ScopeEntry>? Scopes { get; set; }
    public int? TierHotDays { get; set; }
    public int? TierWarmDays { get; set; }
    public List<string>? Tools { get; set; }
    public MemoryFeatures? Memory { get; set; }
    public string? RohlikUsername { get; set; }
    public string? RohlikPassword { get; set; }
    public string? RohlikBaseUrl { get; set; }
}
