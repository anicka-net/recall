using System.Security.Cryptography;
using System.Text;
using Recall.Server;
using Recall.Server.Rohlik;
using Recall.Server.Tools;
using Recall.Storage;

namespace Recall.Tests;

public sealed class ReviewRegressionTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DiaryDatabase _db;
    private readonly RecallConfig _config;

    private const string GuardianSecret = "guardian-secret";
    private const string CodingSecret = "coding-secret";
    private const string AlphaSecret = "alpha-secret";
    private const string BetaSecret = "beta-secret";

    public ReviewRegressionTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"recall-review-{Guid.NewGuid():N}.db");
        _db = new DiaryDatabase(_dbPath);
        _config = new RecallConfig
        {
            GuardianSecretHash = Hash(GuardianSecret),
            CodingSecretHash = Hash(CodingSecret),
            Scopes =
            [
                new ScopeEntry { Name = "alpha", SecretHash = Hash(AlphaSecret) },
                new ScopeEntry { Name = "beta", SecretHash = Hash(BetaSecret) },
            ],
        };
    }

    [Fact]
    public void Coding_cannot_get_or_update_scoped_entry_by_id()
    {
        var scopedId = _db.WriteEntry("Scoped entry", scope: "alpha", restricted: false);

        var getResult = DiaryTools.Entry(_db, _config,
            action: "get", id: scopedId, secret: CodingSecret);
        var updateResult = DiaryTools.Entry(_db, _config,
            action: "update", id: scopedId, content: "changed", secret: CodingSecret);

        Assert.Equal($"Entry #{scopedId} not found or not accessible.", getResult);
        Assert.Equal($"Entry #{scopedId} not found or not accessible.", updateResult);
        Assert.Equal("Scoped entry", _db.GetEntry(scopedId)!.Content);
    }

    [Fact]
    public void Scoped_user_cannot_get_other_scope_entry_by_id()
    {
        var betaId = _db.WriteEntry("Beta entry", scope: "beta", restricted: false);

        var result = DiaryTools.Entry(_db, _config,
            action: "get", id: betaId, secret: AlphaSecret);

        Assert.Equal($"Entry #{betaId} not found or not accessible.", result);
    }

    [Fact]
    public void Scoped_user_can_pin_own_entry_but_not_other_scope()
    {
        var alphaId = _db.WriteEntry("Alpha entry", scope: "alpha", restricted: false);
        var betaId = _db.WriteEntry("Beta entry", scope: "beta", restricted: false);

        var okResult = DiaryTools.Entry(_db, _config,
            action: "pin", id: alphaId, pin: true, secret: AlphaSecret);
        var crossResult = DiaryTools.Entry(_db, _config,
            action: "pin", id: betaId, pin: true, secret: AlphaSecret);

        Assert.Equal($"Entry #{alphaId} marked as pinned.", okResult);
        Assert.Equal($"Entry #{betaId} not found or not accessible.", crossResult);
    }

    [Fact]
    public void Scoped_user_cannot_mark_foundational()
    {
        var alphaId = _db.WriteEntry("Alpha entry", scope: "alpha", restricted: false);

        var result = DiaryTools.Entry(_db, _config,
            action: "pin", id: alphaId, foundational: true, secret: AlphaSecret);

        Assert.Equal("Only guardian can mark entries as foundational.", result);
    }

    [Fact]
    public void Diary_tools_do_not_log_access_secret()
    {
        var originalErr = Console.Error;
        using var buffer = new StringWriter();
        Console.SetError(buffer);
        try
        {
            var result = DiaryTools.Entry(_db, _config,
                action: "write", content: "Test entry", secret: CodingSecret);

            Assert.Contains("Entry #", result);
            Assert.DoesNotContain(CodingSecret, buffer.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("first4", buffer.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalErr);
        }
    }

    [Fact]
    public void Novelty_candidates_respect_access_filters()
    {
        _db.WriteEntry("Public entry", restricted: false);
        _db.WriteEntry("Restricted entry", restricted: true);
        _db.WriteEntry("Scoped alpha entry", scope: "alpha", restricted: false);

        Assert.Equal(2, _db.CountNoveltyCandidates(AccessLevel.Guardian, null));
        Assert.Equal(1, _db.CountNoveltyCandidates(AccessLevel.Coding, null));
        Assert.Equal(1, _db.CountNoveltyCandidates(AccessLevel.Scoped, "alpha"));
    }

    [Fact]
    public void Rohlik_manual_confirmation_code_is_verified_per_client_session()
    {
        var clientA = new RohlikClient("a@example.com", "pw", "https://example.com");
        var clientB = new RohlikClient("b@example.com", "pw", "https://example.com");

        var cartA = new CartContent
        {
            TotalPrice = 123.45,
            Products =
            [
                new CartItem { ProductId = "1", Quantity = 2, Price = 20.50 },
                new CartItem { ProductId = "2", Quantity = 1, Price = 82.45 },
            ],
        };
        var cartB = new CartContent
        {
            TotalPrice = 10,
            Products = [new CartItem { ProductId = "9", Quantity = 1, Price = 10 }],
        };

        clientA.BeginCheckoutSession(cartA);
        clientB.BeginCheckoutSession(cartB);
        var codeA = clientA.IssuePaymentConfirmationCode();

        Assert.True(clientA.VerifyPaymentConfirmationCode(codeA));
        Assert.False(clientA.VerifyPaymentConfirmationCode("wrong"));
        Assert.False(clientB.VerifyPaymentConfirmationCode(codeA));
        Assert.True(clientA.HasMatchingCheckoutSession(cartA));
        Assert.False(clientB.HasMatchingCheckoutSession(cartA));
        Assert.Equal(123.45, clientA.GetCheckoutTotal(), 2);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        if (File.Exists(_dbPath + "-wal")) File.Delete(_dbPath + "-wal");
        if (File.Exists(_dbPath + "-shm")) File.Delete(_dbPath + "-shm");
    }

    private static string Hash(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
