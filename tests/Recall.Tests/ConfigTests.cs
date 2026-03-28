using Recall.Server;
using Xunit;

namespace Recall.Tests;

public sealed class ConfigTests : IDisposable
{
    private readonly string? _originalHome = Environment.GetEnvironmentVariable("HOME");

    [Fact]
    public void Load_ExpandsHomeRelativePathsFromConfig()
    {
        var home = Path.Combine(Path.GetTempPath(), $"recall-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(home, ".recall"));

        var promptPath = Path.Combine(home, ".recall", "prompt.txt");
        File.WriteAllText(promptPath, "test prompt");

        var configPath = Path.Combine(home, ".recall", "config.json");
        File.WriteAllText(configPath, """
            {
              "databasePath": "~/.recall/custom.db",
              "modelPath": "~/.recall/models/custom-model",
              "promptFile": "~/.recall/prompt.txt"
            }
            """);

        Environment.SetEnvironmentVariable("HOME", home);

        var config = RecallConfig.Load();

        Assert.Equal(Path.Combine(home, ".recall", "custom.db"), config.DatabasePath);
        Assert.Equal(Path.Combine(home, ".recall", "models", "custom-model"), config.ModelPath);
        Assert.Equal("test prompt", config.SystemPrompt);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HOME", _originalHome);
    }
}
