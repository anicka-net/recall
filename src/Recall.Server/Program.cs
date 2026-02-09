using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Recall.Server;
using Recall.Storage;

var builder = Host.CreateApplicationBuilder(args);

// CRITICAL: stdout is reserved for MCP protocol. All logging goes to stderr.
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

// Load config and register services for DI into tool methods
var config = RecallConfig.Load();
builder.Services.AddSingleton(config);
builder.Services.AddSingleton(new DiaryDatabase(config.DatabasePath));

// MCP server with stdio transport - auto-discovers [McpServerToolType] classes
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "recall",
            Version = "1.0.0",
        };
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
