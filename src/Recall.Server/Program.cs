// Recall MCP Server - Personal diary with persistent memory
//
// Modes:
//   dotnet run                        # stdio transport (for Claude Code)
//   dotnet run -- --http              # HTTP transport (for claude.ai / remote)
//   dotnet run -- --http --port 3001  # HTTP on custom port
//   dotnet run -- key create "name"   # Create an API key
//   dotnet run -- key list            # List API keys
//   dotnet run -- key revoke 3        # Revoke key by ID

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Recall.Server;
using Recall.Storage;

// ── Parse CLI arguments ──────────────────────────────────────
var httpMode = args.Contains("--http");
var port = 3000;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--port" && i + 1 < args.Length)
        int.TryParse(args[i + 1], out port);
}

// ── Key management commands ──────────────────────────────────
if (args.Length >= 1 && args[0] == "key")
{
    var config = RecallConfig.Load();
    using var db = new DiaryDatabase(config.DatabasePath);

    if (args.Length >= 3 && args[1] == "create")
    {
        var name = args[2];
        var (rawKey, id) = db.CreateApiKey(name);
        Console.WriteLine($"Created API key #{id} ({name}):");
        Console.WriteLine($"  {rawKey}");
        Console.WriteLine();
        Console.WriteLine("Save this key now - it cannot be retrieved later.");
        Console.WriteLine("Use as: Authorization: Bearer <key>");
    }
    else if (args[1] == "list")
    {
        var keys = db.ListApiKeys();
        if (keys.Count == 0)
        {
            Console.WriteLine("No API keys. Create one with: dotnet run -- key create \"my-key\"");
            return 0;
        }
        Console.WriteLine("API Keys:");
        foreach (var k in keys)
        {
            var status = k.Revoked ? "REVOKED" : "active";
            var lastUsed = k.LastUsed is not null
                ? $"last used {k.LastUsed[..10]}"
                : "never used";
            Console.WriteLine($"  #{k.Id,-4} {k.Name,-20} {status,-8} {lastUsed}");
        }
    }
    else if (args.Length >= 3 && args[1] == "revoke" && int.TryParse(args[2], out var keyId))
    {
        if (db.RevokeApiKey(keyId))
            Console.WriteLine($"Revoked API key #{keyId}.");
        else
            Console.WriteLine($"Key #{keyId} not found or already revoked.");
    }
    else
    {
        Console.WriteLine("Usage: dotnet run -- key <create name|list|revoke id>");
    }
    return 0;
}

// ── OAuth setup command ──────────────────────────────────────
if (args.Length >= 2 && args[0] == "oauth" && args[1] == "setup")
{
    Console.Write("Enter passphrase for OAuth login: ");
    var passphrase = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(passphrase))
    {
        Console.WriteLine("Passphrase cannot be empty.");
        return 1;
    }

    var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(passphrase)));

    // Read existing config, add/update the hash (preserve all existing values)
    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var configPath = Path.Combine(home, ".recall", "config.json");
    JsonNode? configNode = null;
    if (File.Exists(configPath))
    {
        try { configNode = JsonNode.Parse(File.ReadAllText(configPath)); }
        catch { /* start fresh */ }
    }
    configNode ??= new JsonObject();
    var configObj = configNode.AsObject();

    configObj["oAuthPassphraseHash"] = hash;
    if (!configObj.ContainsKey("oAuthBaseUrl"))
    {
        Console.Write("Enter public URL for this server (e.g. https://example.com): ");
        var baseUrl = Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(baseUrl))
            configObj["oAuthBaseUrl"] = baseUrl.TrimEnd('/');
    }

    File.WriteAllText(configPath, configObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("OAuth passphrase configured.");
    Console.WriteLine($"Config updated: {configPath}");
    return 0;
}

// ── MCP Server ───────────────────────────────────────────────
var recallConfig = RecallConfig.Load();
var embeddings = new EmbeddingService(recallConfig.ModelPath);
var diaryDb = new DiaryDatabase(recallConfig.DatabasePath, embeddings);
diaryDb.BackfillEmbeddings();

if (httpMode)
{
    // HTTP/SSE transport - for remote access (claude.ai, etc.)
    var builder = WebApplication.CreateBuilder(args);

    builder.Services.AddSingleton(recallConfig);
    builder.Services.AddSingleton(diaryDb);

    builder.Services
        .AddMcpServer(options =>
        {
            options.ServerInfo = new() { Name = "recall", Version = "1.0.0" };
        })
        .WithHttpTransport()
        .WithToolsFromAssembly();

    var app = builder.Build();

    // Auth middleware: check Bearer token (API key or OAuth) for MCP endpoints
    app.Use(async (context, next) =>
    {
        var path = context.Request.Path.Value ?? "";

        // Skip auth for health check, OAuth endpoints, and discovery
        if (path == "/health"
            || path.StartsWith("/oauth/")
            || path.StartsWith("/.well-known/"))
        {
            await next();
            return;
        }

        // Only enforce auth if API keys or OAuth tokens exist
        var hasAuth = diaryDb.HasApiKeys() || !string.IsNullOrEmpty(recallConfig.OAuthPassphraseHash);
        if (hasAuth)
        {
            var authHeader = context.Request.Headers.Authorization.ToString();
            if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = 401;
                var baseUrl = recallConfig.OAuthBaseUrl ?? $"http://127.0.0.1:{port}";
                context.Response.Headers["WWW-Authenticate"] =
                    $"""Bearer resource_metadata="{baseUrl}/.well-known/oauth-protected-resource" """;
                await context.Response.WriteAsync("Authentication required");
                return;
            }

            var token = authHeader["Bearer ".Length..].Trim();
            // Try API key first, then OAuth token
            if (!diaryDb.ValidateApiKey(token) && !diaryDb.ValidateOAuthToken(token))
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsync("Invalid or expired token");
                return;
            }
        }

        await next();
    });

    // OAuth 2.1 endpoints
    OAuthEndpoints.Map(app, diaryDb, recallConfig);

    app.MapMcp();
    app.MapGet("/health", () => "ok");

    var hasApiKeys = diaryDb.HasApiKeys();
    var hasOAuth = !string.IsNullOrEmpty(recallConfig.OAuthPassphraseHash);
    var authStatus = (hasApiKeys, hasOAuth) switch
    {
        (true, true) => "API keys + OAuth",
        (true, false) => "API keys only",
        (false, true) => "OAuth only",
        _ => "disabled (run: dotnet run -- oauth setup)",
    };

    Console.Error.WriteLine($"Recall MCP server (HTTP) listening on port {port}");
    Console.Error.WriteLine($"Database: {recallConfig.DatabasePath}");
    Console.Error.WriteLine($"Auth: {authStatus}");
    if (hasOAuth)
        Console.Error.WriteLine($"OAuth base URL: {recallConfig.OAuthBaseUrl ?? "(not set — run oauth setup)"}");

    app.Run($"http://127.0.0.1:{port}");
}
else
{
    // stdio transport - for Claude Code local connection
    var builder = Host.CreateApplicationBuilder(args);

    // CRITICAL: stdout is MCP protocol only. Kill ALL default loggers, add stderr-only.
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(options =>
    {
        options.LogToStandardErrorThreshold = LogLevel.Trace;
    });

    builder.Services.AddSingleton(recallConfig);
    builder.Services.AddSingleton(diaryDb);

    builder.Services
        .AddMcpServer(options =>
        {
            options.ServerInfo = new() { Name = "recall", Version = "1.0.0" };
        })
        .WithStdioServerTransport()
        .WithToolsFromAssembly();

    await builder.Build().RunAsync();
}

return 0;
