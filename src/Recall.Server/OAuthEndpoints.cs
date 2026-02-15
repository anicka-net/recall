using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Recall.Storage;

namespace Recall.Server;

/// <summary>
/// OAuth 2.1 with PKCE endpoints for Claude.ai MCP authentication.
/// Implements: RFC 9728 (Protected Resource Metadata), RFC 8414 (AS Metadata),
/// RFC 7591 (Dynamic Client Registration), RFC 7636 (PKCE).
/// </summary>
public static class OAuthEndpoints
{
    public static void Map(WebApplication app, DiaryDatabase db, RecallConfig config)
    {
        var baseUrl = config.OAuthBaseUrl ?? $"http://127.0.0.1:3000";

        // ── Discovery ────────────────────────────────────────

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

        // ── Dynamic Client Registration ──────────────────────

        app.MapPost("/oauth/register", async (HttpContext ctx) =>
        {
            var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);

            var clientName = body.TryGetProperty("client_name", out var n) ? n.GetString() : null;

            string[] redirectUris;
            if (body.TryGetProperty("redirect_uris", out var uris) && uris.ValueKind == JsonValueKind.Array)
            {
                redirectUris = uris.EnumerateArray().Select(u => u.GetString()!).ToArray();
            }
            else
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new { error = "invalid_client_metadata", error_description = "redirect_uris required" });
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

        // ── Authorization ────────────────────────────────────

        app.MapGet("/oauth/authorize", (HttpContext ctx) =>
        {
            // Validate required parameters
            var clientId = ctx.Request.Query["client_id"].ToString();
            var redirectUri = ctx.Request.Query["redirect_uri"].ToString();
            var responseType = ctx.Request.Query["response_type"].ToString();
            var codeChallenge = ctx.Request.Query["code_challenge"].ToString();
            var codeChallengeMethod = ctx.Request.Query["code_challenge_method"].ToString();
            var state = ctx.Request.Query["state"].ToString();
            var scope = ctx.Request.Query["scope"].ToString();

            if (responseType != "code")
                return Results.BadRequest(new { error = "unsupported_response_type" });

            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(redirectUri) || string.IsNullOrEmpty(codeChallenge))
                return Results.BadRequest(new { error = "invalid_request", error_description = "Missing required parameters" });

            if (codeChallengeMethod != "S256")
                return Results.BadRequest(new { error = "invalid_request", error_description = "Only S256 code_challenge_method supported" });

            // Verify client exists
            var client = db.GetOAuthClient(clientId);
            if (client is null)
                return Results.BadRequest(new { error = "invalid_client" });

            // Verify redirect_uri is registered
            var registeredUris = JsonSerializer.Deserialize<string[]>(client.RedirectUrisJson) ?? [];
            if (!registeredUris.Contains(redirectUri))
                return Results.BadRequest(new { error = "invalid_redirect_uri" });

            // Check if passphrase is configured
            if (string.IsNullOrEmpty(config.OAuthPassphraseHash))
                return Results.Text(
                    "OAuth not configured. Run: dotnet run -- oauth setup",
                    "text/plain", statusCode: 500);

            // Serve login form
            var html = LoginFormHtml(clientId, redirectUri, codeChallenge, state, scope);
            return Results.Content(html, "text/html");
        });

        app.MapPost("/oauth/authorize", async (HttpContext ctx) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            var passphrase = form["passphrase"].ToString();
            var clientId = form["client_id"].ToString();
            var redirectUri = form["redirect_uri"].ToString();
            var codeChallenge = form["code_challenge"].ToString();
            var state = form["state"].ToString();
            var scope = form["scope"].ToString();

            // Validate passphrase
            var hash = HashString(passphrase);
            if (hash != config.OAuthPassphraseHash)
            {
                var html = LoginFormHtml(clientId, redirectUri, codeChallenge, state, scope,
                    error: "Invalid passphrase. Try again.");
                return Results.Content(html, "text/html");
            }

            // Generate authorization code
            var code = db.CreateAuthCode(clientId, redirectUri, codeChallenge,
                string.IsNullOrEmpty(scope) ? null : scope);

            // Redirect back to client with code
            var separator = redirectUri.Contains('?') ? "&" : "?";
            var redirect = $"{redirectUri}{separator}code={Uri.EscapeDataString(code)}";
            if (!string.IsNullOrEmpty(state))
                redirect += $"&state={Uri.EscapeDataString(state)}";

            return Results.Redirect(redirect);
        });

        // ── Token Exchange ───────────────────────────────────

        app.MapPost("/oauth/token", async (HttpContext ctx) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            var grantType = form["grant_type"].ToString();

            if (grantType == "authorization_code")
            {
                var code = form["code"].ToString();
                var codeVerifier = form["code_verifier"].ToString();
                var clientId = form["client_id"].ToString();

                if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(codeVerifier))
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsJsonAsync(new { error = "invalid_request" });
                    return;
                }

                // Consume code (single-use)
                var codeInfo = db.ConsumeAuthCode(code);
                if (codeInfo is null)
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsJsonAsync(new { error = "invalid_grant", error_description = "Code expired or already used" });
                    return;
                }

                // Verify client_id matches
                if (!string.IsNullOrEmpty(clientId) && clientId != codeInfo.ClientId)
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsJsonAsync(new { error = "invalid_grant", error_description = "Client mismatch" });
                    return;
                }

                // Verify PKCE: SHA256(code_verifier) must match code_challenge
                var computedChallenge = ComputeS256Challenge(codeVerifier);
                if (computedChallenge != codeInfo.CodeChallenge)
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsJsonAsync(new { error = "invalid_grant", error_description = "PKCE verification failed" });
                    return;
                }

                // Issue tokens
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
            else if (grantType == "refresh_token")
            {
                var refreshToken = form["refresh_token"].ToString();
                if (string.IsNullOrEmpty(refreshToken))
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsJsonAsync(new { error = "invalid_request" });
                    return;
                }

                var clientId = db.ConsumeRefreshToken(refreshToken);
                if (clientId is null)
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsJsonAsync(new { error = "invalid_grant", error_description = "Refresh token expired or revoked" });
                    return;
                }

                var tokens = db.CreateTokenPair(clientId, "recall");
                await ctx.Response.WriteAsJsonAsync(new
                {
                    access_token = tokens.AccessToken,
                    token_type = "Bearer",
                    expires_in = tokens.ExpiresIn,
                    refresh_token = tokens.RefreshToken,
                    scope = "recall",
                });
            }
            else
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new { error = "unsupported_grant_type" });
            }
        });
    }

    // ── PKCE S256 ────────────────────────────────────────────

    private static string ComputeS256Challenge(string codeVerifier)
    {
        var bytes = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string HashString(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    // ── Login Form ───────────────────────────────────────────

    private static string LoginFormHtml(
        string clientId, string redirectUri, string codeChallenge,
        string state, string scope, string? error = null)
    {
        var errorHtml = error is not null
            ? $"""<p style="color:#c0392b;margin-bottom:16px">{error}</p>"""
            : "";

        return $$"""
            <!DOCTYPE html>
            <html>
            <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>Recall - Authorize</title>
                <style>
                    body { font-family: system-ui, sans-serif; background: #1a1a2e; color: #e0e0e0;
                           display: flex; justify-content: center; align-items: center; min-height: 100vh; margin: 0; }
                    .card { background: #16213e; border-radius: 12px; padding: 32px; max-width: 400px; width: 90%;
                            box-shadow: 0 4px 24px rgba(0,0,0,0.3); }
                    h1 { margin: 0 0 8px; font-size: 1.4em; color: #e0e0e0; }
                    .subtitle { color: #888; margin-bottom: 24px; font-size: 0.9em; }
                    label { display: block; margin-bottom: 6px; font-size: 0.9em; color: #aaa; }
                    input[type="password"] { width: 100%; padding: 10px 12px; border: 1px solid #333;
                           border-radius: 6px; background: #0f3460; color: #e0e0e0; font-size: 1em;
                           box-sizing: border-box; }
                    input[type="password"]:focus { outline: none; border-color: #e94560; }
                    button { width: 100%; padding: 10px; margin-top: 16px; background: #e94560;
                             color: white; border: none; border-radius: 6px; font-size: 1em;
                             cursor: pointer; }
                    button:hover { background: #c0392b; }
                </style>
            </head>
            <body>
                <div class="card">
                    <h1>Recall</h1>
                    <p class="subtitle">Authorize access to your diary</p>
                    {{errorHtml}}
                    <form method="POST" action="/oauth/authorize">
                        <input type="hidden" name="client_id" value="{{clientId}}">
                        <input type="hidden" name="redirect_uri" value="{{redirectUri}}">
                        <input type="hidden" name="code_challenge" value="{{codeChallenge}}">
                        <input type="hidden" name="state" value="{{state}}">
                        <input type="hidden" name="scope" value="{{scope}}">
                        <label for="passphrase">Passphrase</label>
                        <input type="password" id="passphrase" name="passphrase" autofocus required>
                        <button type="submit">Authorize</button>
                    </form>
                </div>
            </body>
            </html>
            """;
    }
}
