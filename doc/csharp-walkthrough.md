# Recall: C# Walkthrough for Experienced Developers

A guide to the C# and .NET concepts used in Recall, aimed at developers who
know software design and MCP but are new to the .NET ecosystem.

The project has 4 key files, explained here in dependency order.

---

## 1. Schema (`Recall.Storage/Schema.cs`)

```csharp
public static class Schema
{
    private const string CreateTablesSql = """
        PRAGMA journal_mode = 'wal';
        ...
        """;

    public static void Initialize(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = CreateTablesSql;
        cmd.ExecuteNonQuery();
    }

    public static SqliteConnection CreateConnection(string dbPath)
    {
        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Pooling = true,
        }.ToString();

        var conn = new SqliteConnection(connStr);
        conn.Open();
        Initialize(conn);
        return conn;
    }
}
```

### Concepts

**`public static class`** - Can't be instantiated. All members must be `static`.
Think of it as a namespace with functions - like a Python module with only
module-level functions. You can't do `new Schema()`.

**`private const string CreateTablesSql = """`** - The `"""` is a **raw string
literal** (C# 11+). Everything between the triple quotes is literal, no escaping
needed. Like Python's `"""..."""` but with one extra trick: the indentation of
the closing `"""` determines how much leading whitespace is stripped.

**`using var cmd = connection.CreateCommand();`** - The C# equivalent of Python's
`with` statement. `using` means "call `.Dispose()` on this object when it goes
out of scope." `SqliteCommand` holds native resources (prepared statement
handle), and `using` ensures cleanup. Without `var`, you'd write
`using SqliteCommand cmd = ...` - `var` just means "infer the type."

**`SqliteConnectionStringBuilder { ... }.ToString()`** - A **builder pattern
with object initializer**. Instead of hand-writing
`"Data Source=/path;Pooling=true"`, you set properties and call `.ToString()`.
The `{ DataSource = ..., Pooling = true }` syntax constructs the object then
sets properties, all in one expression.

### The SQL

The FTS5 virtual table with `content=entries, content_rowid=id` is a **content
table** - FTS5 doesn't store its own copy of the data, it just indexes the
`entries` table. The triggers keep the FTS index in sync on INSERT/UPDATE/DELETE.
The `WHERE revoked = 0` on the api_keys index is a **partial index** - only
indexes active keys, so lookups skip revoked ones entirely.

---

## 2. Database Layer (`Recall.Storage/DiaryDatabase.cs`)

```csharp
public record DiaryEntry(
    int Id,
    DateTimeOffset CreatedAt,
    string Content,
    string? Tags,
    string? ConversationId);

public class DiaryDatabase : IDisposable
{
    private readonly SqliteConnection _conn;

    public DiaryDatabase(string dbPath)
    {
        _conn = Schema.CreateConnection(dbPath);
    }

    public int WriteEntry(string content, string? tags = null, ...)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Parameters.AddWithValue("@tags", (object?)tags ?? DBNull.Value);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public (string RawKey, int Id) CreateApiKey(string name) { ... }

    public void Dispose() { ... }
}
```

### Concepts

**`public record DiaryEntry(...)`** - A **record** is C#'s immutable data class.
This one line gives you: a constructor, readonly properties, value equality
(`==` compares all fields, not reference), `ToString()`, and destructuring.
Like Python's `@dataclass(frozen=True)` or Kotlin's `data class`. The `string?`
means "nullable string" - C# tracks nullability at the type level.

**`: IDisposable`** - This class implements the `IDisposable` interface -
it promises to have a `Dispose()` method. Anything holding native resources
(DB connections, file handles) should implement it. Then callers can use
`using var db = new DiaryDatabase(...)` and it auto-disposes when scope ends.

**`private readonly SqliteConnection _conn;`** - `readonly` means it can only
be set in the constructor. The `_conn` naming with underscore prefix is the C#
convention for private fields (like Python's `self._conn`). There's no `self`
in C# - instance members are accessed directly by name.

**`(object?)tags ?? DBNull.Value`** - Two things. `??` is the **null-coalescing
operator**: "use the left side unless it's null, then use the right." And
`DBNull.Value` is ADO.NET's way of representing SQL NULL - you can't pass C#
`null` directly to `AddWithValue`, you need to cast to `object?` first and
coalesce to `DBNull`. This is one of the uglier corners of the ADO.NET API.

**`public (string RawKey, int Id) CreateApiKey`** - A **tuple return type**.
Instead of creating a whole class just to return two values, you return
`(string, int)` with names. The caller gets `result.RawKey` and `result.Id`.
Like Python's returning a tuple but with named fields.

**`words.Where(w => !w.Equals("AND", ...))`** - **LINQ with a lambda**.
`.Where()` is like Python's `filter()`. The `w =>` is a lambda expression
(like Python's `lambda w:`). LINQ is C#'s answer to list comprehensions -
you chain `.Where()`, `.Select()` (map), `.OrderBy()`, `.Take()` (limit), etc.

**The Dispose pattern** - `GC.SuppressFinalize(this)` tells the garbage
collector "don't bother calling my finalizer, I already cleaned up." Without
it, the GC would queue the object for finalization even though `Dispose()`
already freed everything. The `_disposed` flag prevents double-dispose.

---

## 3. Config (`Recall.Server/Config.cs`)

```csharp
public class RecallConfig
{
    public string DatabasePath { get; init; } = "";
    public int AutoContextLimit { get; init; } = 5;

    public static RecallConfig Load()
    {
        var file = JsonSerializer.Deserialize<ConfigFile>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        return new RecallConfig
        {
            DatabasePath = file?.DatabasePath ?? defaultPath,
            AutoContextLimit = file?.AutoContextLimit ?? 5,
        };
    }
}

public class ConfigFile
{
    public string? DatabasePath { get; set; }
    public int? AutoContextLimit { get; set; }
}
```

### Concepts

**`{ get; init; }` vs `{ get; set; }`** - The two config classes show the
difference. `RecallConfig` uses `init` - properties can be set during
construction (with the object initializer syntax) but are **immutable after**.
`ConfigFile` uses `set` - mutable, because the JSON deserializer needs to
write to properties after construction.

**Two-class pattern** - `ConfigFile` has all nullable properties (`string?`,
`int?`) because any field might be missing from JSON. `RecallConfig` has
non-nullable properties with defaults. The `Load()` method bridges the two,
coalescing nulls with `??`. This way the rest of the code never worries about
missing config values.

**`file?.PromptFile`** - The `?.` is the **null-conditional operator**. If
`file` is null, the whole expression is null without throwing. Chains nicely:
`file?.PromptFile?.StartsWith(...)`.

**`file.PromptFile[2..]`** - **Range operator**. `[2..]` means "from index 2
to end." Like Python's `[2:]`. Used here to strip the `~/` prefix.

**`JsonSerializer.Deserialize<ConfigFile>(json)`** - The `<ConfigFile>` is a
**generic type argument**. It tells the deserializer what type to create.
`PropertyNameCaseInsensitive = true` lets `"databasePath"` in JSON match
`DatabasePath` in C#.

---

## 4. MCP Tools (`Recall.Server/Tools/DiaryTools.cs`)

```csharp
[McpServerToolType]
public class DiaryTools
{
    [McpServerTool(Name = "diary_write")]
    [Description("Write a diary entry.")]
    public static string Write(
        DiaryDatabase db,
        [Description("The diary entry text")] string content,
        [Description("Optional tags")] string? tags = null)
    {
        var id = db.WriteEntry(content, tags);
        return $"Entry #{id} saved.";
    }
}
```

This is where the MCP magic happens.

### Concepts

**`[McpServerToolType]` and `[McpServerTool]`** - These are **attributes**
(like Python decorators, or Java annotations). The MCP SDK scans assemblies
for classes with `[McpServerToolType]` and methods with `[McpServerTool]`.
It auto-generates the JSON schema from the method signature - parameter names,
types, descriptions, defaults all become the tool's `inputSchema`.

**DI injection in static methods** - This is the cleverest part of the design.
The methods are `static` but take `DiaryDatabase db` and `RecallConfig config`
as parameters. These are NOT passed by the MCP client - they're **injected by
the Dependency Injection container**. The SDK sees that `DiaryDatabase` is
registered as a service (via `AddSingleton` in Program.cs), so it pulls it
from DI automatically. Only parameters with `[Description]` become tool
arguments visible to Claude. Parameters without descriptions that match
registered services are injected silently. This is how one line of service
registration makes the database available to all tools.

**`$"{now:yyyy-MM-dd HH:mm:ss zzz} ({now:dddd})"`** - **String interpolation
with format specifiers**. The `:yyyy-MM-dd` after the variable is a format
string. `dddd` = full day name (Monday). `zzz` = timezone offset (+01:00).
Like Python's f-strings but with .NET format strings built in.

**`Guid.NewGuid().ToString("N")[..12]`** - Generates a random GUID, formats
as 32 hex chars (no dashes with `"N"`), takes first 12. Quick unique
conversation ID.

**`seen.Add(e.Id)` in dedup** - `HashSet.Add()` returns `bool` - `true` if
the item was new, `false` if already present. So `if (seen.Add(e.Id))` is a
one-step deduplicate idiom.

**`recent.Concat(relevant)`** - LINQ again. `.Concat()` joins two sequences
lazily (like Python's `itertools.chain`). `.OrderByDescending()`, `.Take()`,
`.ToList()` chain to sort, limit, and materialize.

---

## 5. Program.cs - The Wiring

```csharp
// HTTP mode
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(recallConfig);
builder.Services.AddSingleton(diaryDb);
builder.Services.AddMcpServer(options => { ... })
    .WithHttpTransport()
    .WithToolsFromAssembly();
var app = builder.Build();
app.Use(async (context, next) => { /* auth middleware */ });
app.MapMcp();
app.Run($"http://127.0.0.1:{port}");

// Stdio mode
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();  // CRITICAL
builder.Services.AddMcpServer(options => { ... })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();
await builder.Build().RunAsync();
```

### Concepts

**Top-level statements** - There's no `class Program` or `static void Main()`.
C# 10+ lets you write code directly at the top level. The `args` variable
exists implicitly. `return 0;` exits the process. The whole file IS main.

**`builder.Services.AddSingleton(recallConfig)`** - **Dependency Injection
registration**. `AddSingleton` says "whenever anyone asks for a `RecallConfig`,
give them this exact instance." This is the link to DiaryTools - when the MCP
SDK needs to call `Write(DiaryDatabase db, ...)`, it asks the DI container
"give me a `DiaryDatabase`" and gets the singleton registered here.

Three DI lifetimes exist: `AddSingleton` (one instance forever), `AddScoped`
(one per request), `AddTransient` (new instance every time).

**Two builder patterns** - The most important architectural point:

- **HTTP mode**: `WebApplication.CreateBuilder(args)` - ASP.NET Core's web
  host. Gives you HTTP, routing, middleware pipeline, Kestrel web server.
  `.WithHttpTransport()` plugs MCP into that.

- **Stdio mode**: `Host.CreateApplicationBuilder(args)` - The generic host.
  No web server, no HTTP. Just DI + service lifetime management.
  `.WithStdioServerTransport()` reads JSON-RPC from stdin, writes to stdout.

Both share the same DI pattern. The tools code doesn't know or care which
transport it's running on.

**`app.Use(async (context, next) => { ... })`** - ASP.NET **middleware**. A
function wrapping every HTTP request. `context` has the request/response.
Calling `next()` passes to the next middleware. Not calling it stops the
request (returning 401/403). This middleware checks API key Bearer tokens.
Note: claude.ai doesn't do simple Bearer tokens - it requires OAuth 2.1
with PKCE (see `oauth-walkthrough.md`). The middleware handles both.

**`async`/`await`** - C#'s async model, similar to Python's `async`/`await`.
`async` marks a method as asynchronous, `await` suspends until a task
completes. The middleware must be async because HTTP handling is inherently
asynchronous.

**`builder.Logging.ClearProviders()`** - The hard-won lesson. The Web SDK
adds default console loggers that write to stdout. In stdio MCP mode, stdout
is the protocol channel - any stray logging corrupts the JSON-RPC stream.
So we nuke all loggers and add back only stderr logging. Without this one
line, stdio mode silently breaks.

**`.WithToolsFromAssembly()`** - Scans the compiled DLL for classes with
`[McpServerToolType]` and registers all their `[McpServerTool]` methods.
Zero manual tool registration - add the attributes and they're discovered.

---

## The Big Picture

The design separates concerns through DI:

- **Storage layer** (`Schema`, `DiaryDatabase`) knows nothing about MCP
- **Tools** (`DiaryTools`) know nothing about transport (HTTP vs stdio)
- **Program.cs** wires them together through dependency injection

This is why we could add HTTP mode without touching DiaryTools or
DiaryDatabase. And why the same tool code serves both Claude Code (stdio)
and any HTTP client without changes. (Claude.ai specifically requires
OAuth 2.1 - see `oauth-walkthrough.md` for that layer.)
