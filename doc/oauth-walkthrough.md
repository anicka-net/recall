# OAuth 2.1 Walkthrough: C# for the Confused Linux Engineer

How Recall got authentication so Claude.ai can connect securely over the
internet. Read `csharp-walkthrough.md` first - this builds on those
concepts.

The problem: Recall was running on a public server with no auth. Claude
Code connects locally via stdio (no auth needed), but Claude.ai connects
over HTTP. Bearer tokens aren't enough - Claude.ai's MCP integration
requires a full OAuth 2.1 flow with PKCE. So we built a minimal OAuth
provider directly in the server.

---

## The OAuth Dance

Before diving into code, here's what happens when Claude.ai connects:

```
1. Claude.ai → GET /mcp             → 401 + WWW-Authenticate header
2. Claude.ai → GET /.well-known/oauth-protected-resource    → "go ask this auth server"
3. Claude.ai → GET /.well-known/oauth-authorization-server  → "here are my endpoints"
4. Claude.ai → POST /oauth/register  → "hi, I'm Claude, here's my callback URL"
5. Claude.ai → opens your browser    → GET /oauth/authorize  → HTML login form
6. You type passphrase               → POST /oauth/authorize → redirect with code
7. Claude.ai → POST /oauth/token     → exchanges code for access_token
8. Claude.ai → GET /mcp + Bearer token → authenticated, MCP session starts
9. (later)   → POST /oauth/token     → refresh_token → new access_token
```

Steps 1-4 happen automatically. You only see step 6: a login form in
your browser asking for a passphrase.

---

## 1. Discovery Endpoints (`OAuthEndpoints.cs`)

```csharp
app.MapGet("/.well-known/oauth-protected-resource", () =>
    Results.Json(new
    {
        resource = baseUrl,
        authorization_servers = new[] { baseUrl },
        scopes_supported = new[] { "recall" },
    }));

app.MapGet("/.well-known/oauth-authorization-server", () =>
    Results.Json(new
    {
        issuer = baseUrl,
        authorization_endpoint = $"{baseUrl}/oauth/authorize",
        token_endpoint = $"{baseUrl}/oauth/token",
        registration_endpoint = $"{baseUrl}/oauth/register",
        response_types_supported = new[] { "code" },
        grant_types_supported = new[] { "authorization_code", "refresh_token" },
        code_challenge_methods_supported = new[] { "S256" },
        token_endpoint_auth_methods_supported = new[] { "none" },
        scopes_supported = new[] { "recall" },
    }));
```

### Concepts

**`app.MapGet(path, handler)`** - ASP.NET minimal API routing. Maps an
HTTP GET request to a handler function. The handler here is a **lambda**
(the `() =>` part). Think of it as Flask's `@app.route("/path")` but
without decorators - you call `MapGet` directly on the app object.

**`Results.Json(new { ... })`** - Returns a JSON response. The `new { }`
creates an **anonymous type** - an unnamed class the compiler generates
on the fly. You define properties inline: `new { resource = baseUrl }`
becomes `{"resource": "https://..."}`. It's a one-off throwaway type
that exists only for serialization. Like Python's `jsonify({"key": val})`
but type-safe at compile time.

**`new[] { "code" }`** - Array literal with type inference. The compiler
sees `"code"` is a string, so it creates `string[]`. The `new[]` syntax
infers the element type. (C# 12's collection expressions `["code"]`
would also work, but we're using the older syntax for clarity.)

**Why two well-known endpoints** - RFC 9728 (Protected Resource Metadata)
tells the client "this resource is protected, ask that authorization
server." RFC 8414 (Authorization Server Metadata) tells the client what
the server supports and where the endpoints are. Together they let
Claude.ai auto-discover everything without hardcoded URLs. This is how
OAuth scales: the client doesn't need to know your server's URL scheme.

### The RFC 8414 Path Gotcha

RFC 8414 says the well-known URL is constructed by inserting
`/.well-known/oauth-authorization-server` between the host and path of
the issuer. So if the issuer is `https://example.com/recall`, the client
looks for `https://example.com/.well-known/oauth-authorization-server/recall`
- NOT `https://example.com/recall/.well-known/oauth-authorization-server`.

When running behind a reverse proxy at `/recall/`, this means the
well-known URLs live outside the proxy prefix. The reverse proxy needs
explicit rules to catch them. This is the kind of thing that's invisible
in local testing (no path prefix) and breaks in production.

---

## 2. Dynamic Client Registration

```csharp
app.MapPost("/oauth/register", async (HttpContext ctx) =>
{
    var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);

    var clientName = body.TryGetProperty("client_name", out var n) ? n.GetString() : null;

    string[] redirectUris;
    if (body.TryGetProperty("redirect_uris", out var uris)
        && uris.ValueKind == JsonValueKind.Array)
    {
        redirectUris = uris.EnumerateArray().Select(u => u.GetString()!).ToArray();
    }
    else
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { error = "invalid_client_metadata" });
        return;
    }

    var urisJson = JsonSerializer.Serialize(redirectUris);
    var clientId = db.RegisterOAuthClient(clientName, urisJson);

    ctx.Response.StatusCode = 201;
    await ctx.Response.WriteAsJsonAsync(new
    {
        client_id = clientId,
        client_name = clientName,
        redirect_uris = redirectUris,
        token_endpoint_auth_method = "none",
    });
});
```

### Concepts

**`async (HttpContext ctx) =>`** - An async lambda taking the HTTP
context. `HttpContext` is ASP.NET's everything-object for a request -
contains `Request` (headers, body, query string), `Response` (status
code, headers, body), and more. Like WSGI's `environ` or Express's
`(req, res)` combined into one object.

**`JsonSerializer.DeserializeAsync<JsonElement>`** - Deserializes JSON
from a stream without knowing the shape in advance. `JsonElement` is the
DOM type - like Python's `json.loads()` returning a dict. Compare with
`Deserialize<ConfigFile>` which maps to a specific class. Use
`JsonElement` when the structure varies or you only need a few fields.

**`body.TryGetProperty("client_name", out var n)`** - The `Try` pattern
again (see vector-search-walkthrough). Returns `true` if the property
exists, sets `n` to its value. If it doesn't exist, returns `false` and
`n` is default. This avoids exceptions for missing JSON properties -
important for untrusted input from the internet.

**`uris.EnumerateArray().Select(u => u.GetString()!).ToArray()`** - LINQ
chain on a JSON array. `EnumerateArray()` yields each element as a
`JsonElement`. `.Select()` maps each to a string. `.ToArray()`
materializes. The `!` after `GetString()` is the null-forgiving operator
(we already checked `ValueKind == Array`, so elements exist).

**`ctx.Response.StatusCode = 201`** - Sets HTTP status directly. No
`return Results.Created(...)` because we're in a void-returning async
lambda using `WriteAsJsonAsync`. Two styles coexist in ASP.NET: the
`Results.*` helpers (used in the discovery endpoints) and direct response
manipulation (used here). Direct manipulation is more flexible but
more verbose.

**Why dynamic registration** - Claude.ai doesn't know our server in
advance. It discovers us, then registers itself as an OAuth client with
its callback URL. The server generates a `client_id` and remembers the
registration. This is RFC 7591 (Dynamic Client Registration) and it's
what makes the whole thing zero-configuration on Claude.ai's side.

---

## 3. Authorization: The Login Form

```csharp
app.MapGet("/oauth/authorize", (HttpContext ctx) =>
{
    var clientId = ctx.Request.Query["client_id"].ToString();
    var redirectUri = ctx.Request.Query["redirect_uri"].ToString();
    var codeChallenge = ctx.Request.Query["code_challenge"].ToString();
    var codeChallengeMethod = ctx.Request.Query["code_challenge_method"].ToString();
    var state = ctx.Request.Query["state"].ToString();

    if (codeChallengeMethod != "S256")
        return Results.BadRequest(new { error = "invalid_request" });

    // Verify client exists and redirect_uri is registered
    var client = db.GetOAuthClient(clientId);
    if (client is null)
        return Results.BadRequest(new { error = "invalid_client" });

    var registeredUris = JsonSerializer.Deserialize<string[]>(client.RedirectUrisJson) ?? [];
    if (!registeredUris.Contains(redirectUri))
        return Results.BadRequest(new { error = "invalid_redirect_uri" });

    var html = LoginFormHtml(clientId, redirectUri, codeChallenge, state, scope);
    return Results.Content(html, "text/html");
});
```

### Concepts

**`ctx.Request.Query["code_challenge"]`** - Query string access. Returns
a `StringValues` (can hold multiple values for the same key). `.ToString()`
collapses it to a single string. Empty string if not present (not null).
This is why we check `string.IsNullOrEmpty()` rather than `!= null`.

**`client is null`** - **Pattern matching** for null. Equivalent to
`client == null` but preferred in modern C# because `is null` is always
a true null check. The `==` operator can be overloaded by a class to do
something weird. `is null` cannot be overloaded. For `is not null`, same
story.

**`?? []`** - Null coalescing with a **collection expression**. If
`Deserialize<string[]>` returns null, use an empty array. The `[]` is
C# 12 syntax for an empty collection.

**`Results.Content(html, "text/html")`** - Returns raw HTML. This is how
the server serves the login form without a templating engine or Razor
pages. The entire HTML is built in a helper method as a raw string. Not
elegant, but for a single page it's simpler than adding a view engine
dependency.

**Redirect URI validation** - Before showing the login form, we verify
that the `redirect_uri` matches what the client registered. This
prevents an attacker from registering a client, then changing the
redirect to point to their own server to steal the auth code.

### The Form POST

```csharp
app.MapPost("/oauth/authorize", async (HttpContext ctx) =>
{
    var form = await ctx.Request.ReadFormAsync();
    var passphrase = form["passphrase"].ToString();

    var hash = HashString(passphrase);
    if (hash != config.OAuthPassphraseHash)
    {
        var html = LoginFormHtml(..., error: "Invalid passphrase. Try again.");
        return Results.Content(html, "text/html");
    }

    var code = db.CreateAuthCode(clientId, redirectUri, codeChallenge,
        string.IsNullOrEmpty(scope) ? null : scope);

    var separator = redirectUri.Contains('?') ? "&" : "?";
    var redirect = $"{redirectUri}{separator}code={Uri.EscapeDataString(code)}";
    if (!string.IsNullOrEmpty(state))
        redirect += $"&state={Uri.EscapeDataString(state)}";

    return Results.Redirect(redirect);
});
```

**`ctx.Request.ReadFormAsync()`** - Reads `application/x-www-form-urlencoded`
POST data. Returns an `IFormCollection` indexed by field name. The `await`
is needed because the body might still be streaming from the client.

**`Uri.EscapeDataString(code)`** - URL-encodes a string. The auth code is
hex so it doesn't actually need encoding, but always encoding user-facing
URLs is good hygiene. Like Python's `urllib.parse.quote()`.

**`Results.Redirect(redirect)`** - Returns HTTP 302 Found. The browser
follows this redirect to Claude.ai's callback URL, carrying the auth
code in the query string. Claude.ai never sees the passphrase - it only
gets the code.

**Why `<form method="POST">` without an action** - The form posts to its
own URL (the current page). This matters behind a reverse proxy. If the
form had `action="/oauth/authorize"`, the browser would post to an
absolute path, bypassing the proxy prefix. Omitting `action` makes it
always post back to whatever URL the browser is currently showing.

---

## 4. PKCE: Proof Key for Code Exchange

```csharp
private static string ComputeS256Challenge(string codeVerifier)
{
    var bytes = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
    return Convert.ToBase64String(bytes)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
}
```

### What PKCE Solves

Without PKCE, the OAuth flow has a vulnerability: the auth code travels
through the user's browser as a URL parameter. A malicious app on the
same device could intercept it and exchange it for a token.

PKCE (RFC 7636, pronounced "pixie") fixes this:
1. Claude.ai generates a random `code_verifier` (a secret it keeps)
2. Claude.ai computes `code_challenge = Base64URL(SHA256(code_verifier))`
3. Claude.ai sends the `code_challenge` to the authorize endpoint
4. Server stores the challenge with the auth code
5. When exchanging the code for a token, Claude.ai sends the original
   `code_verifier`
6. Server computes SHA256 of the verifier and checks it matches the
   stored challenge

An interceptor has the code but not the verifier, so they can't
complete step 5.

### Concepts

**`SHA256.HashData(Encoding.ASCII.GetBytes(...))`** - Static method on
`SHA256`. No need to create an instance - `HashData` is a convenience
method added in .NET 5. `Encoding.ASCII.GetBytes()` converts string to
bytes. ASCII (not UTF-8) because the PKCE spec defines the verifier as
ASCII characters only.

**Base64URL encoding** - Standard Base64 uses `+`, `/`, and `=` padding.
URLs don't like those characters. Base64URL replaces `+` with `-`, `/`
with `_`, and strips `=` padding. There's no built-in `ToBase64UrlString`
in .NET (there is in newer versions, but we're being explicit), so we
chain `TrimEnd('=').Replace('+', '-').Replace('/', '_')`. Ugly but clear.

---

## 5. Token Exchange

```csharp
app.MapPost("/oauth/token", async (HttpContext ctx) =>
{
    var form = await ctx.Request.ReadFormAsync();
    var grantType = form["grant_type"].ToString();

    if (grantType == "authorization_code")
    {
        var code = form["code"].ToString();
        var codeVerifier = form["code_verifier"].ToString();

        var codeInfo = db.ConsumeAuthCode(code);
        if (codeInfo is null) { /* 400 invalid_grant */ }

        var computedChallenge = ComputeS256Challenge(codeVerifier);
        if (computedChallenge != codeInfo.CodeChallenge) { /* 400 PKCE failed */ }

        var tokens = db.CreateTokenPair(codeInfo.ClientId, codeInfo.Scope);
        await ctx.Response.WriteAsJsonAsync(new
        {
            access_token = tokens.AccessToken,
            token_type = "Bearer",
            expires_in = tokens.ExpiresIn,
            refresh_token = tokens.RefreshToken,
            scope = codeInfo.Scope ?? "recall",
        });
    }
    else if (grantType == "refresh_token") { ... }
});
```

### Concepts

**`db.ConsumeAuthCode(code)`** - "Consume" means the code is single-use.
The database marks it as used and returns the stored info (client_id,
code_challenge, etc.). Calling it again returns null. This prevents
replay attacks where someone reuses a captured auth code.

**Two grant types in one endpoint** - OAuth uses a single `/token`
endpoint for multiple operations, distinguished by `grant_type`. The
`authorization_code` grant exchanges a fresh auth code for tokens. The
`refresh_token` grant exchanges an expiring refresh token for new tokens.
Pattern matching on `grantType` routes to the right logic.

---

## 6. The Database Layer

### Token Storage: Hash, Never Store Raw

```csharp
public OAuthTokenPair CreateTokenPair(string clientId, string? scope)
{
    var accessToken = $"recall_at_{GenerateRandomKey(32)}";
    var refreshToken = $"recall_rt_{GenerateRandomKey(32)}";

    using var cmd = _conn.CreateCommand();
    cmd.CommandText = """
        INSERT INTO oauth_tokens (token_hash, client_id, token_type, scope, expires_at, created_at)
        VALUES (@hash, @client, 'access', @scope, @expires, @now);
        """;
    cmd.Parameters.AddWithValue("@hash", HashKey(accessToken));
    ...

    return new OAuthTokenPair(accessToken, refreshToken, 3600);
}

private static string HashKey(string rawKey)
{
    var bytes = System.Text.Encoding.UTF8.GetBytes(rawKey);
    var hash = SHA256.HashData(bytes);
    return Convert.ToHexStringLower(hash);
}

private static string GenerateRandomKey(int length)
{
    var bytes = RandomNumberGenerator.GetBytes(length);
    return Convert.ToHexStringLower(bytes);
}
```

**The critical pattern**: the raw token is returned to the client, but
only the SHA256 hash is stored in the database. Same pattern used for
API keys (see csharp-walkthrough). If the database leaks, the attacker
gets hashes, not usable tokens. On every request, the server hashes the
incoming Bearer token and looks up the hash. Like storing password
hashes instead of passwords.

**`$"recall_at_{...}"`** - Token prefixes (`recall_at_` for access,
`recall_rt_` for refresh) make tokens self-describing. You can tell at
a glance what kind of token you're looking at. GitHub does this too
(`ghp_` for personal access tokens, `gho_` for OAuth tokens).

**`RandomNumberGenerator.GetBytes(length)`** - Cryptographically secure
random bytes. This is .NET's equivalent of `/dev/urandom` or Python's
`secrets.token_bytes()`. Never use `Random` for security-sensitive
values - it's predictable.

**`Convert.ToHexStringLower(bytes)`** - .NET 8+ convenience method. Turns
`byte[]` into a lowercase hex string. Before this existed, you'd write
`BitConverter.ToString(bytes).Replace("-", "").ToLower()` or loop
manually. Small quality-of-life improvement.

### Refresh Token Rotation

```csharp
public string? ConsumeRefreshToken(string rawToken)
{
    var hash = HashKey(rawToken);
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = """
        SELECT client_id, scope, expires_at FROM oauth_tokens
        WHERE token_hash = @hash AND token_type = 'refresh' AND revoked = 0
        """;
    cmd.Parameters.AddWithValue("@hash", hash);
    using var reader = cmd.ExecuteReader();
    if (!reader.Read()) return null;

    var clientId = reader.GetString(0);
    var expiresAt = reader.GetString(2);
    reader.Close();

    if (DateTimeOffset.Parse(expiresAt) < DateTimeOffset.UtcNow)
        return null;

    // Revoke used refresh token (rotation)
    using var revoke = _conn.CreateCommand();
    revoke.CommandText = "UPDATE oauth_tokens SET revoked = 1 WHERE token_hash = @hash";
    revoke.Parameters.AddWithValue("@hash", hash);
    revoke.ExecuteNonQuery();

    return clientId;
}
```

**Token rotation**: each refresh token is single-use. When Claude.ai
refreshes, the old refresh token is revoked and a new pair is issued.
If an attacker steals a refresh token and uses it, the legitimate
client's next refresh fails (token already revoked), signaling
compromise. Without rotation, a stolen refresh token grants indefinite
access.

**`reader.Close()`** - Explicit close before the UPDATE. SQLite allows
only one active reader per connection. The update would fail if the
reader were still open. With the `using` statement, the reader would
close at end of scope, but we need it closed *now*. This is a SQLite
constraint, not a general ADO.NET one.

---

## 7. The Auth Middleware

```csharp
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";

    // Skip auth for discovery and OAuth endpoints
    if (path == "/health"
        || path.StartsWith("/oauth/")
        || path.StartsWith("/.well-known/"))
    {
        await next();
        return;
    }

    var hasAuth = diaryDb.HasApiKeys()
        || !string.IsNullOrEmpty(recallConfig.OAuthPassphraseHash);
    if (hasAuth)
    {
        var authHeader = context.Request.Headers.Authorization.ToString();
        if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 401;
            context.Response.Headers["WWW-Authenticate"] =
                $"""Bearer resource_metadata="{baseUrl}/.well-known/oauth-protected-resource" """;
            await context.Response.WriteAsync("Authentication required");
            return;
        }

        var token = authHeader["Bearer ".Length..].Trim();
        if (!diaryDb.ValidateApiKey(token) && !diaryDb.ValidateOAuthToken(token))
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsync("Invalid or expired token");
            return;
        }
    }

    await next();
});
```

### Concepts

**`app.Use()`** - Registers middleware in the ASP.NET pipeline. Every
HTTP request passes through every middleware in order. Each middleware
can: inspect the request, modify it, short-circuit (return without
calling `next()`), or pass through. This middleware sits before
`MapMcp()`, so all MCP requests must pass auth first. Think of it as
iptables for HTTP: rules evaluated in order, first match wins.

**`await next()`** - Passes the request to the next middleware. If you
don't call this, the request stops here. Our middleware calls `next()`
for allowed requests (health check, OAuth endpoints, authenticated
requests) and returns early (without `next()`) for rejected ones.

**`authHeader["Bearer ".Length..]`** - **Range operator** again. Extracts
everything after `"Bearer "` (7 characters). `[7..]` means "from index
7 to end." Like Python's `authHeader[7:]`.

**`context.Response.Headers["WWW-Authenticate"]`** - This header is the
key to the whole OAuth dance. When Claude.ai gets a 401 with this
header, it knows: (1) authentication is needed, (2) it should fetch
the resource metadata URL to learn how to authenticate. Without this
header, Claude.ai would just see "access denied" and give up.

**Try API key first, then OAuth** - `ValidateApiKey(token)` checks the
old API key table. `ValidateOAuthToken(token)` checks the new OAuth
token table. Both auth mechanisms work simultaneously. Existing API key
users aren't broken by adding OAuth. Short-circuit evaluation (`&&`)
means if the API key matches, we never check the OAuth table.

**`StringComparison.OrdinalIgnoreCase`** - HTTP headers are
case-insensitive per spec. `"bearer"`, `"Bearer"`, and `"BEARER"` are
all valid. `Ordinal` means byte-by-byte comparison (no locale-specific
rules). Always use `OrdinalIgnoreCase` for protocol strings, never
`CurrentCultureIgnoreCase` (which might do Turkish-I shenanigans).

---

## 8. The OAuth Setup CLI

```csharp
if (args.Length >= 2 && args[0] == "oauth" && args[1] == "setup")
{
    Console.Write("Enter passphrase for OAuth login: ");
    var passphrase = Console.ReadLine()?.Trim();

    var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(passphrase)));

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
        Console.Write("Enter public URL (e.g. https://example.com): ");
        var baseUrl = Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(baseUrl))
            configObj["oAuthBaseUrl"] = baseUrl.TrimEnd('/');
    }

    File.WriteAllText(configPath, configObj.ToJsonString(
        new JsonSerializerOptions { WriteIndented = true }));
}
```

### Concepts

**`JsonNode` vs `JsonElement` vs `Deserialize<T>`** - Three ways to
handle JSON in .NET, each for a different purpose:
- `Deserialize<ConfigFile>` - maps JSON to a known C# class. Best when
  you control the schema. Used in `RecallConfig.Load()`.
- `JsonElement` - read-only DOM. Good for inspecting unknown JSON. Used
  in the registration endpoint.
- `JsonNode` - read-write DOM. Good for modifying JSON without losing
  unknown fields. Used here. You can set `configObj["key"] = value`
  and serialize back.

**Why `JsonNode` matters here** - An early version used
`Deserialize<Dictionary<string, object>>` to read the config, add the
hash, and write it back. This silently lost the types of existing values
(integers became `JsonElement` objects, nulls disappeared). `JsonNode`
preserves everything exactly as it was - you add your field and the rest
is untouched. This bug was found the hard way when the passphrase hash
was saved but the database path disappeared.

**`configNode ??= new JsonObject()`** - **Null-coalescing assignment**.
If `configNode` is null (file doesn't exist or parse failed), assign a
new empty JSON object. Otherwise keep what we parsed. Like Python's
`configNode = configNode or {}` but null-specific (doesn't trigger on
empty).

---

## 9. Schema Migration

```csharp
if (version < 3)
{
    using var oauth = connection.CreateCommand();
    oauth.CommandText = """
        CREATE TABLE IF NOT EXISTS oauth_clients (
            client_id TEXT PRIMARY KEY,
            client_name TEXT,
            redirect_uris TEXT NOT NULL,
            created_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS oauth_codes (
            code TEXT PRIMARY KEY,
            client_id TEXT NOT NULL,
            redirect_uri TEXT NOT NULL,
            code_challenge TEXT NOT NULL,
            scope TEXT,
            expires_at TEXT NOT NULL,
            used INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS oauth_tokens (
            token_hash TEXT PRIMARY KEY,
            client_id TEXT NOT NULL,
            token_type TEXT NOT NULL,
            scope TEXT,
            expires_at TEXT NOT NULL,
            revoked INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_oauth_tokens_type
            ON oauth_tokens(token_type) WHERE revoked = 0;

        PRAGMA user_version = 3;
        """;
    oauth.ExecuteNonQuery();
}
```

Migration v3, following the same pattern as v1 (embeddings) and v2
(health data). See vector-search-walkthrough for how `PRAGMA user_version`
works.

Three tables, three concerns:
- **`oauth_clients`** - Who is allowed to connect (registered clients).
  `redirect_uris` is a JSON array stored as TEXT - SQLite doesn't have
  an array type, so we serialize.
- **`oauth_codes`** - Short-lived authorization codes (5 min, single-use).
  The `used` flag prevents replay.
- **`oauth_tokens`** - Access and refresh tokens, stored as hashes.
  `token_type` distinguishes them. The partial index
  `WHERE revoked = 0` means lookups only scan active tokens.

---

## The Big Picture

The OAuth implementation follows the same design principles as the rest
of Recall:

- **Single file per concern** - `OAuthEndpoints.cs` has all HTTP
  handlers, `DiaryDatabase.cs` has all storage logic
- **Backwards compatible** - existing API keys still work, stdio
  transport unaffected
- **Graceful degradation** - if OAuth isn't configured
  (`OAuthPassphraseHash` is empty), the server runs without auth
- **No external dependencies** - no identity server library, no JWT
  library, just ASP.NET + SHA256 + SQLite

The whole OAuth provider is roughly 300 lines of endpoint code and 170
lines of database code. For a single-user diary server, that's the right
level of complexity - enough to be secure, not enough to need its own
configuration management.
