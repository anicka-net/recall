using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Recall.Server;
using Recall.Storage;
using Xunit;

namespace Recall.Tests;

public sealed class OAuthAndStorageTests
{
    [Fact]
    public void ConsumeRefreshToken_PreservesStoredScope()
    {
        using var db = CreateDatabase();

        var tokens = db.CreateTokenPair("client-1", "scope-a");
        var refreshInfo = db.ConsumeRefreshToken(tokens.RefreshToken);

        Assert.NotNull(refreshInfo);
        Assert.Equal("client-1", refreshInfo!.ClientId);
        Assert.Equal("scope-a", refreshInfo.Scope);
        Assert.Null(db.ConsumeRefreshToken(tokens.RefreshToken));
    }

    [Fact]
    public async Task AuthorizePost_RejectsUnregisteredRedirectUri()
    {
        using var db = CreateDatabase();
        var clientId = db.RegisterOAuthClient("test-client", """["https://good.example/callback"]""");

        await using var app = await CreateOAuthApp(db);
        var client = app.GetTestClient();

        var response = await client.PostAsync("/oauth/authorize", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["passphrase"] = "secret-passphrase",
            ["client_id"] = clientId,
            ["redirect_uri"] = "https://evil.example/callback",
            ["code_challenge"] = "challenge",
            ["state"] = "state-1",
            ["scope"] = "recall",
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync();
        Assert.Contains("invalid_redirect_uri", payload);
    }

    [Fact]
    public async Task TokenExchange_RejectsRedirectUriMismatch()
    {
        using var db = CreateDatabase();
        var clientId = db.RegisterOAuthClient("test-client", """["https://good.example/callback"]""");
        var code = db.CreateAuthCode(clientId, "https://good.example/callback", ComputeChallenge("verifier"), "scope-a");

        await using var app = await CreateOAuthApp(db);
        var client = app.GetTestClient();

        var response = await client.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = "verifier",
            ["client_id"] = clientId,
            ["redirect_uri"] = "https://evil.example/callback",
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync();
        Assert.Contains("invalid_grant", payload);
        Assert.Contains("Redirect URI mismatch", payload);
    }

    private static DiaryDatabase CreateDatabase()
    {
        var path = Path.Combine(Path.GetTempPath(), $"recall-tests-{Guid.NewGuid():N}.db");
        return new DiaryDatabase(path);
    }

    private static async Task<WebApplication> CreateOAuthApp(DiaryDatabase db)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        var config = new RecallConfig
        {
            DatabasePath = "",
            ModelPath = "",
            OAuthBaseUrl = "https://recall.example",
            OAuthPassphraseHash = HashString("secret-passphrase"),
        };

        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton(config);

        var app = builder.Build();
        OAuthEndpoints.Map(app, db, config);
        await app.StartAsync();
        return app;
    }

    private static string ComputeChallenge(string verifier)
    {
        var bytes = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
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
}
