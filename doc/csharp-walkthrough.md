# Recall: C# Walkthrough for Experienced Developers

A guide to the C# and .NET concepts used in Recall, aimed at developers who
know software design and MCP but are new to the .NET ecosystem.

The project has grown from 4 core files to about 10, but the architecture
still follows the same layering: **storage → tools → wiring**.

---

## 1. Schema (`Recall.Storage/Schema.cs`)

```csharp
public static class Schema
{
    private const string CreateTablesSql = """
        PRAGMA journal_mode = 'wal';
        PRAGMA busy_timeout = 5000;

        CREATE TABLE IF NOT EXISTS entries ( ... );
        CREATE VIRTUAL TABLE IF NOT EXISTS entries_fts USING fts5( ... );
        CREATE TABLE IF NOT EXISTS api_keys ( ... );
        """;

    private static void Migrate(SqliteConnection conn) { ... }

    public static void Initialize(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = CreateTablesSql;
        cmd.ExecuteNonQuery();
        Migrate(connection);
    }
}
```

### Concepts

**`public static class`** - Can't be instantiated. All members must be `static`.
Think of it as a namespace with functions - like a Python module with only
module-level functions.

**Raw string literals** (`"""..."""`) - C# 11+. Everything between the triple
quotes is literal, no escaping needed. The indentation of the closing `"""`
determines how much leading whitespace is stripped.

**`using var cmd = connection.CreateCommand();`** - The C# equivalent of Python's
`with` statement. `using` means "call `.Dispose()` when scope ends." `var` just
means "infer the type."

### The SQL

The FTS5 virtual table with `content=entries, content_rowid=id` is a **content
table** - FTS5 doesn't store its own copy of the data, it just indexes the
`entries` table. The triggers keep the FTS index in sync on
INSERT/UPDATE/DELETE.

### Migration System

`Migrate()` checks a `schema_version` table and runs incremental DDL:

- **v0**: base schema (entries, FTS5, api_keys)
- **v1**: embedding BLOB column for vector search
- **v2**: health_data table (Fitbit metrics)
- **v3**: OAuth 2.1 tables (clients, codes, tokens)
- **v4**: restricted column (privilege separation)
- **v5**: scope column (multi-user isolation)
- **v6**: tier/pinned/foundational columns (memory aging)
- **v7**: calendar table (daily plans/summaries)

Each migration is idempotent (checks version before running). This is a
common C# pattern - keep migrations in code rather than external SQL files,
so the schema is always in sync with the binary.

---

## 2. Access Control & Database (`Recall.Storage/DiaryDatabase.cs`)

This is the largest file (~1,360 lines). It handles everything from basic
CRUD to vector search, tiered memory aging, and OAuth token management.

### Access Levels

```csharp
public enum AccessLevel { None, Scoped, Coding, Guardian }
```

Four-tier hierarchy. Secrets map to levels via SHA256 hash comparison:

```csharp
public (AccessLevel Level, string? Scope) ResolveAccess(
    string? secret, string? guardianHash, string? codingHash,
    IReadOnlyList<ScopeEntry>? scopes = null)
{
    if (!hasAnyAuth) return (AccessLevel.Guardian, null);  // local-only
    if (string.IsNullOrEmpty(secret)) return (AccessLevel.None, null);

    var hash = HashKey(secret);
    if (hash == guardianHash) return (AccessLevel.Guardian, null);
    if (hash == codingHash)   return (AccessLevel.Coding, null);
    var scope = scopes?.FirstOrDefault(s => s.SecretHash == hash);
    if (scope != null)        return (AccessLevel.Scoped, scope.Name);
    return (AccessLevel.None, null);
}
```

**Tuple return `(AccessLevel Level, string? Scope)`** - Instead of creating a
class just to return two values, you return a named tuple. The caller gets
`result.Level` and `result.Scope`.

### SQL Access Filters

The cleverest pattern in the codebase - a function that returns a SQL fragment
plus a parameter binder:

```csharp
private static (string Filter, Action<SqliteCommand>? Bind) AccessFilter(
    AccessLevel level, string? scope, string prefix = "AND")
{
    return level switch
    {
        AccessLevel.Guardian when scope != null =>
            ($" {prefix} scope = @scope",
             cmd => cmd.Parameters.AddWithValue("@scope", scope)),
        AccessLevel.Guardian =>
            ($" {prefix} scope IS NULL", null),
        AccessLevel.Coding =>
            ($" {prefix} scope IS NULL AND restricted = 0", null),
        AccessLevel.Scoped =>
            ($" {prefix} scope = @scope",
             cmd => cmd.Parameters.AddWithValue("@scope", scope!)),
        _ => ($" {prefix} 1 = 0", null),
    };
}
```

**`Action<SqliteCommand>?`** - A nullable delegate type. `Action<T>` means
"a function that takes a `T` and returns void." Here it's a callback that
binds parameters to a command. The `?` means it can be null (Guardian with
no scope needs no parameter binding).

**Pattern matching with `when`** - The `switch` expression matches on both
the enum value AND a condition. `Guardian when scope != null` is a different
arm than plain `Guardian`. This compiles to efficient code - no
if/else chains.

**`scope!`** - The null-forgiving operator. Tells the compiler "I know this
isn't null here, trust me." Used when the `when` guard already checked, but
the compiler can't prove it.

Every query that touches entries calls `AccessFilter()` and appends the
result to its WHERE clause. One central check, used everywhere:

```csharp
public bool CanAccessEntry(int id, AccessLevel level, string? scope)
{
    var (filter, bind) = AccessFilter(level, scope);
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = $"SELECT COUNT(*) FROM entries WHERE id = @id{filter}";
    cmd.Parameters.AddWithValue("@id", id);
    bind?.Invoke(cmd);
    return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
}
```

**`bind?.Invoke(cmd)`** - Null-conditional method call. If `bind` is null
(no parameters needed), this is a no-op. If it's a delegate, it runs.

### Records

```csharp
public record DiaryEntry(
    int Id,
    DateTimeOffset CreatedAt,
    string Content,
    string? Tags,
    string? ConversationId);
```

A **record** is C#'s immutable data class. This one line gives you: a
constructor, readonly properties, value equality (`==` compares all fields),
`ToString()`, and destructuring. Like Python's `@dataclass(frozen=True)`.
The `string?` means "nullable string" - C# tracks nullability at the type
level.

### Embedding & Vector Search

The database optionally stores float[] embeddings as BLOBs. When available,
search uses cosine similarity:

```csharp
var similarity = CosineSimilarity(queryEmbedding, entryEmbedding);
if (useRif) similarity *= (1.0 - Math.Min(retrievalCount * 0.03, 0.15));
```

**RIF (Retrieval-Induced Forgetting)** - Frequently retrieved entries get a
scoring penalty (up to 15%). This prevents the same entries from dominating
every search. The retrieval count is tracked per entry and bumped lazily.

When embeddings aren't available, search falls back gracefully to LIKE
queries. This fallback chain (vector → FTS5 → LIKE → tag match) runs in
every search call.

### Memory Aging

Entries move through tiers based on age:

- **Hot (tier 0)**: Recent (< hotDays). Always returned in context.
- **Warm (tier 1)**: Medium age. Returned when relevant.
- **Cold (tier 2+)**: Old. Compressed to first ~15 words ("temporal gist").

```csharp
public int RunAging(int hotDays = 7, int warmDays = 90, ...)
```

Pinned and foundational entries never age out. Foundational entries form a
"core knowledge index" that's shown at the start of every context call.

---

## 3. Config (`Recall.Server/Config.cs`)

```csharp
public class RecallConfig
{
    public string DatabasePath { get; init; } = "";
    public string? GuardianSecretHash { get; init; }
    public string? CodingSecretHash { get; init; }
    public List<ScopeEntry> Scopes { get; init; } = [];
    public MemoryFeatures Memory { get; init; } = new();
    public string? RohlikUsername { get; init; }
    // ...

    public MemoryFeatures GetMemoryFeatures(string? scopeName)
    {
        var scopeOverride = scopeName != null
            ? Scopes.FirstOrDefault(s => ...)?.Memory : null;
        return MemoryFeatures.Resolve(scopeOverride, Memory);
    }

    public bool IsToolEnabled(string module) =>
        Tools.Count == 0 || Tools.Contains(module, StringComparer.OrdinalIgnoreCase);
}
```

### Concepts

**`{ get; init; }` vs `{ get; set; }`** - `init` means the property can be
set during construction but is **immutable after**. `RecallConfig` uses `init`
(the config doesn't change at runtime). The JSON deserialization target
`ConfigFile` uses `set` because the deserializer writes to properties after
construction.

**`MemoryFeatures.Resolve(scopeOverride, global)`** - A static method that
merges two config layers: per-scope overrides fall back to global defaults.
Each field uses `??` (null-coalescing): `scopeOverride?.Rate ?? global?.Rate
?? 0.05`. Three levels of fallback in one expression.

**`IsToolEnabled` with expression body** - The `=>` syntax is a shorthand
for single-expression methods. `Tools.Count == 0` means "empty list = all
enabled." This is the feature-flag pattern: Rohlik tools only load if both
credentials are present AND "rohlik" is in the tools list (or the list is
empty).

### Two-Class Pattern

`ConfigFile` has all nullable properties (`string?`, `int?`) because any
field might be missing from JSON. `RecallConfig` has non-nullable properties
with defaults. The `Load()` method bridges them, coalescing nulls with `??`.
The rest of the code never worries about missing config values.

---

## 4. MCP Tools (`Recall.Server/Tools/DiaryTools.cs`)

```csharp
[McpServerToolType]
public class DiaryTools
{
    [McpServerTool(Name = "diary")]
    [Description("Write, update, get, or pin diary entries.")]
    public static string Entry(
        DiaryDatabase db,
        RecallConfig config,
        [Description("Action: write, update, get, pin")] string action = "write",
        [Description("Entry content")] string? content = null,
        [Description("Entry ID")] int? id = null,
        [Description("Access secret")] string? secret = null)
    {
        var (access, userScope) = db.ResolveAccess(
            secret, config.GuardianSecretHash, config.CodingSecretHash, config.Scopes);
        if (access == AccessLevel.None)
            return "Access denied. Provide a valid secret.";

        return action switch
        {
            "write" => DoWrite(db, config, content, access, userScope),
            "update" when id != null => DoUpdate(db, id.Value, content, access, userScope),
            "get" when id != null => DoGet(db, id.Value, access, userScope),
            "pin" when id != null => DoPin(db, id.Value, access),
            _ => "Unknown action or missing parameters."
        };
    }
}
```

### Concepts

**`[McpServerToolType]` and `[McpServerTool]`** - **Attributes** (like Python
decorators). The MCP SDK scans for these and auto-generates the JSON schema
from the method signature. Parameter names, types, descriptions, and defaults
all become the tool's `inputSchema`.

**DI injection in static methods** - The methods are `static` but take
`DiaryDatabase db` and `RecallConfig config` as parameters. These are NOT
passed by the MCP client - they're **injected by the Dependency Injection
container**. The SDK sees that `DiaryDatabase` is registered as a service
(via `AddSingleton` in Program.cs), so it pulls it from DI automatically.
Only parameters with `[Description]` become tool arguments visible to Claude.
Parameters without descriptions that match registered services are injected
silently.

**Action-based consolidation** - Instead of separate tools for write, update,
get, and pin, there's one tool with an `action` parameter. This reduces the
tool schema overhead in conversation context. The `switch` expression with
`when` guards routes to private helper methods.

**Access control per call** - Every tool call starts with `ResolveAccess()`
which maps the secret to an access level. The level then gates what operations
are allowed (e.g., only Guardian can pin entries).

### Context Loading

The `diary_search` tool with `action=context` is called at conversation start.
It does several things in one call:

1. Runs aging (promotes entries through tiers)
2. Builds a foundational index (pinned entries, guardian-only)
3. Merges recent (hot tier) + relevant (warm tier) entries
4. Shows tier counts so the agent knows how much memory exists

This is the entry point for the cognitive-science features: involuntary
recall (random cold entry surfaces ~5% of the time), RIF scoring, and
reconsolidation drift (top search results get tagged with the query terms).

---

## 5. Health Tools (`Recall.Server/Tools/HealthTools.cs`)

```csharp
[McpServerToolType]
public class HealthTools
{
    [McpServerTool(Name = "health")]
    [Description("Health data. Actions: recent, query, log_migraine, log_period.")]
    public static string Health(
        DiaryDatabase db, RecallConfig config,
        [Description("Action")] string action = "recent",
        [Description("Access secret")] string? secret = null,
        // ...
    )
```

Same pattern as DiaryTools: action-based dispatch, DI injection, access
control. Guardian-only (no scoped or coding access to health data).

**`[GeneratedRegex(...)]`** - C# source-generated regex. Instead of
compiling the pattern at runtime, the compiler generates optimized matching
code at build time. Used here for date format validation
(`^\d{4}-\d{2}-\d{2}$`).

---

## 6. Rohlik Integration (`Recall.Server/Rohlik/`)

Three files: `RohlikClient.cs` (HTTP client), `RohlikTools.cs` (MCP tools),
`Totp.cs` (payment confirmation).

### RohlikClient.cs

```csharp
public class RohlikClient
{
    private readonly HttpClient _http;
    private readonly CookieContainer _cookies = new();
    private readonly SemaphoreSlim _rateLock = new(1, 1);

    private async Task<T> WithSession<T>(Func<Task<T>> action)
    {
        await EnsureLoggedIn();
        try { return await action(); }
        catch (RohlikException ex) when (ex.StatusCode == 401)
        {
            _loggedIn = false;
            await Login();
            return await action();
        }
    }
}
```

### Concepts

**`SemaphoreSlim`** - A lightweight async-compatible lock. Used for rate
limiting: only one request at a time, with 100ms minimum gap. Unlike `lock`,
it works with `async`/`await`.

**`Func<Task<T>>`** - A delegate that returns a `Task<T>` (an async
operation). `WithSession` wraps any API call with auto-login and 401 retry.
The caller passes a lambda: `WithSession(async () => { ... })`.

**`catch ... when`** - Exception filter. Only catches `RohlikException` if
the status code is 401. Other exceptions propagate normally. This is more
precise than catching all exceptions and re-throwing.

**`CookieContainer`** - Attached to the `HttpClientHandler`, it automatically
manages session cookies across requests. Login sets cookies, subsequent
requests send them.

### RohlikTools.cs

Same MCP pattern as DiaryTools but with a twist: `RohlikClient?` is nullable
in the tool signatures because the client might not be registered (no
credentials configured). Each tool starts with `CheckAccess(client)` which
returns "not configured" if null.

The checkout flow uses instance state on RohlikClient rather than static
fields, ensuring per-session isolation:

```csharp
var fingerprint = client.BeginCheckoutSession(cart);
// ... later ...
if (!client.HasMatchingCheckoutSession(currentCart))
    return "Cart has been modified since submit!";
```

**`CryptographicOperations.FixedTimeEquals`** - Constant-time comparison for
payment confirmation codes. Prevents timing attacks where an attacker could
determine how many characters matched by measuring response time.

---

## 7. Program.cs - The Wiring

```csharp
var recallConfig = RecallConfig.Load();
var embeddings = new EmbeddingService(recallConfig.ModelPath);
var diaryDb = new DiaryDatabase(recallConfig.DatabasePath, embeddings);
diaryDb.BackfillEmbeddings();

Recall.Server.Rohlik.RohlikClient? rohlikClient = null;
if (!string.IsNullOrEmpty(recallConfig.RohlikUsername) && ...)
    rohlikClient = new Recall.Server.Rohlik.RohlikClient(...);

if (httpMode)
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Services.AddSingleton(recallConfig);
    builder.Services.AddSingleton(diaryDb);
    if (rohlikClient != null) builder.Services.AddSingleton(rohlikClient);

    var mcpBuilder = builder.Services.AddMcpServer(...)
        .WithHttpTransport();

    if (recallConfig.IsToolEnabled("diary"))
        mcpBuilder.WithTools<DiaryTools>();
    if (recallConfig.IsToolEnabled("health"))
        mcpBuilder.WithTools<HealthTools>();
    if (rohlikClient != null && recallConfig.IsToolEnabled("rohlik"))
        mcpBuilder.WithTools<RohlikTools>();

    // Auth middleware, OAuth endpoints, MCP proxy...
    app.Run($"http://127.0.0.1:{port}");
}
else
{
    // stdio transport for Claude Code
    var builder = new HostBuilder();
    builder.ConfigureLogging(logging => {
        logging.ClearProviders();  // CRITICAL: stdout is the protocol channel
        logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    });
    // same DI + tool registration...
    await builder.Build().RunAsync();
}
```

### Concepts

**Top-level statements** - There's no `class Program` or `static void Main()`.
C# 10+ lets you write code directly. The `args` variable exists implicitly.

**`AddSingleton`** - DI registration. "Whenever anyone asks for a
`RecallConfig`, give them this exact instance." This is the link to
DiaryTools - when the MCP SDK needs to call `Entry(DiaryDatabase db, ...)`,
it asks the DI container for a `DiaryDatabase` and gets the singleton
registered here.

**Conditional tool registration** - `WithTools<T>()` registers a tool class.
The conditionals mean: no Rohlik credentials → no Rohlik tools in the schema.
Tools list empty → all enabled. This keeps the tool schema clean.

**Two builder patterns** - The most important architectural point:

- **HTTP mode**: `WebApplication.CreateBuilder(args)` - ASP.NET Core's web
  host. HTTP, routing, middleware, SSE transport.
- **Stdio mode**: `new HostBuilder()` - No web server. Just DI + service
  lifetime. `WithStdioServerTransport()` reads JSON-RPC from stdin, writes
  to stdout.

Both share the same DI pattern. The tools don't know which transport they're
running on.

**`logging.ClearProviders()`** - The hard-won lesson. The default console
logger writes to stdout. In stdio mode, stdout is the protocol channel -
any stray logging corrupts JSON-RPC. So we redirect all logging to stderr.

### Auth Middleware

```csharp
app.Use(async (context, next) =>
{
    if (path.StartsWith("/oauth/") || path.StartsWith("/.well-known/"))
    {
        await next();  // skip auth for OAuth discovery
        return;
    }

    var hasAuth = diaryDb.HasApiKeys() || !string.IsNullOrEmpty(recallConfig.OAuthPassphraseHash);
    if (hasAuth)
    {
        var token = authHeader["Bearer ".Length..].Trim();
        if (!diaryDb.ValidateApiKey(token) && !diaryDb.ValidateOAuthToken(token))
        {
            context.Response.StatusCode = 403;
            return;
        }
    }
    await next();
});
```

**ASP.NET middleware** wraps every HTTP request. Calling `next()` passes to
the next handler. Not calling it stops the request. Note the two-layer auth:
this middleware validates the **transport token** (can this client talk to the
server at all?). The **privilege level** (guardian/coding/scoped) is checked
per-tool-call via the `secret` parameter.

**`authHeader["Bearer ".Length..]`** - Range operator. `[7..]` means "from
index 7 to end." Like Python's `[7:]`.

**Tuple pattern** - The auth status message uses a switch on a value tuple:
```csharp
var authStatus = (hasApiKeys, hasOAuth) switch
{
    (true, true)   => "API keys + OAuth",
    (true, false)  => "API keys only",
    (false, true)  => "OAuth only",
    _              => "disabled",
};
```
Pattern matching on multiple booleans at once. Cleaner than nested ifs.

---

## The Big Picture

```
┌─────────────────────────────────────────────┐
│  Program.cs - DI wiring, transport, auth    │
├──────────┬──────────┬───────────────────────┤
│ Diary    │ Health   │ Rohlik                │
│ Tools    │ Tools    │ Tools + Client + TOTP │
├──────────┴──────────┴───────────────────────┤
│  DiaryDatabase - access control, search,    │
│  aging, calendar, health, embeddings, OAuth │
├─────────────────────────────────────────────┤
│  Schema - SQLite DDL + migrations (v0-v7)   │
└─────────────────────────────────────────────┘
```

The design separates concerns through DI:

- **Storage** (`Schema`, `DiaryDatabase`) knows nothing about MCP
- **Tools** know nothing about transport (HTTP vs stdio)
- **Program.cs** wires them together through dependency injection

Tool classes are stateless - all state lives in the singletons injected by
DI. This is why the same tool code serves Claude Code (stdio), claude.ai
(HTTP+OAuth), and the Rohlik grocery API without changes.

The access control model has two independent layers:
1. **Transport auth** (middleware): "Is this client allowed to connect?"
2. **Privilege auth** (per-call secret): "What can this user see?"

This means a single HTTP endpoint serves multiple users at different
privilege levels, with scope isolation enforced at the SQL level.
