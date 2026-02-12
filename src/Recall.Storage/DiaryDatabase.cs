using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Recall.Storage;

public record DiaryEntry(
    int Id,
    DateTimeOffset CreatedAt,
    string Content,
    string? Tags,
    string? ConversationId);

public class DiaryDatabase : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly EmbeddingService? _embeddings;
    private bool _disposed;

    public DiaryDatabase(string dbPath, EmbeddingService? embeddings = null)
    {
        _conn = Schema.CreateConnection(dbPath);
        _embeddings = embeddings;
    }

    public int WriteEntry(string content, string? tags = null, string? conversationId = null,
        string source = "claude-code")
    {
        // Combine content and tags for embedding (tags add searchable context)
        var textToEmbed = string.IsNullOrEmpty(tags) ? content : $"{content}\n{tags}";
        byte[]? embeddingBlob = null;
        if (_embeddings is { IsAvailable: true })
        {
            try { embeddingBlob = EmbeddingService.Serialize(_embeddings.Embed(textToEmbed)); }
            catch { /* non-fatal: entry saved without embedding */ }
        }

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO entries (created_at, content, tags, conversation_id, source, embedding)
            VALUES (@now, @content, @tags, @cid, @source, @emb);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@content", content);
        cmd.Parameters.AddWithValue("@tags", (object?)tags ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cid", (object?)conversationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@source", source);
        cmd.Parameters.AddWithValue("@emb", (object?)embeddingBlob ?? DBNull.Value);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public bool UpdateEntry(int id, string content, string? tags = null)
    {
        // Check if entry exists
        using var checkCmd = _conn.CreateCommand();
        checkCmd.CommandText = "SELECT id FROM entries WHERE id = @id";
        checkCmd.Parameters.AddWithValue("@id", id);
        if (checkCmd.ExecuteScalar() is null) return false;

        // Generate new embedding if available
        var textToEmbed = string.IsNullOrEmpty(tags) ? content : $"{content}\n{tags}";
        byte[]? embeddingBlob = null;
        if (_embeddings is { IsAvailable: true })
        {
            try { embeddingBlob = EmbeddingService.Serialize(_embeddings.Embed(textToEmbed)); }
            catch { /* non-fatal: entry updated without embedding */ }
        }

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE entries
            SET content = @content,
                tags = @tags,
                embedding = @emb
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@content", content);
        cmd.Parameters.AddWithValue("@tags", (object?)tags ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@emb", (object?)embeddingBlob ?? DBNull.Value);
        return cmd.ExecuteNonQuery() > 0;
    }

    public List<DiaryEntry> Search(string query, int limit = 10)
    {
        if (string.IsNullOrWhiteSpace(query))
            return GetRecent(limit);

        // Vector search if embeddings are available
        if (_embeddings is { IsAvailable: true })
        {
            try { return VectorSearch(query, limit); }
            catch { /* fall through to LIKE */ }
        }

        return SearchLike(query, limit);
    }

    private List<DiaryEntry> VectorSearch(string query, int limit)
    {
        var queryEmbedding = _embeddings!.Embed(query);

        // Load all entries that have embeddings
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, created_at, content, tags, conversation_id, embedding
            FROM entries
            WHERE embedding IS NOT NULL
            """;

        var scored = new List<(DiaryEntry Entry, float Score)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var entry = new DiaryEntry(
                Id: reader.GetInt32(0),
                CreatedAt: DateTimeOffset.Parse(reader.GetString(1)),
                Content: reader.GetString(2),
                Tags: reader.IsDBNull(3) ? null : reader.GetString(3),
                ConversationId: reader.IsDBNull(4) ? null : reader.GetString(4));

            var blob = (byte[])reader.GetValue(5);
            var embedding = EmbeddingService.Deserialize(blob);
            var score = EmbeddingService.Similarity(queryEmbedding, embedding);
            scored.Add((entry, score));
        }

        return scored
            .OrderByDescending(x => x.Score)
            .Take(limit)
            .Select(x => x.Entry)
            .ToList();
    }

    public List<DiaryEntry> GetRecent(int count = 10)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, created_at, content, tags, conversation_id
            FROM entries
            ORDER BY created_at DESC
            LIMIT @count
            """;
        cmd.Parameters.AddWithValue("@count", count);
        return ReadEntries(cmd);
    }

    public int GetEntryCount()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM entries";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private List<DiaryEntry> SearchLike(string query, int limit)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, created_at, content, tags, conversation_id
            FROM entries
            WHERE content LIKE @pattern OR tags LIKE @pattern
            ORDER BY created_at DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@pattern", $"%{query}%");
        cmd.Parameters.AddWithValue("@limit", limit);
        return ReadEntries(cmd);
    }

    private static List<DiaryEntry> ReadEntries(SqliteCommand cmd)
    {
        var entries = new List<DiaryEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(new DiaryEntry(
                Id: reader.GetInt32(0),
                CreatedAt: DateTimeOffset.Parse(reader.GetString(1)),
                Content: reader.GetString(2),
                Tags: reader.IsDBNull(3) ? null : reader.GetString(3),
                ConversationId: reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return entries;
    }

    // ── Health Data ──────────────────────────────────────────────

    public HealthEntry? GetHealthByDate(string date)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT date, summary FROM health_data WHERE date = @date";
        cmd.Parameters.AddWithValue("@date", date);
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
            return new HealthEntry(reader.GetString(0), reader.GetString(1));
        return null;
    }

    public List<HealthEntry> GetRecentHealth(int days = 7)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT date, summary FROM health_data
            ORDER BY date DESC LIMIT @days
            """;
        cmd.Parameters.AddWithValue("@days", days);

        var entries = new List<HealthEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            entries.Add(new HealthEntry(reader.GetString(0), reader.GetString(1)));
        return entries;
    }

    public List<HealthEntry> SearchHealth(string query, int limit = 7)
    {
        if (string.IsNullOrWhiteSpace(query))
            return GetRecentHealth(limit);

        if (_embeddings is { IsAvailable: true })
        {
            try { return HealthVectorSearch(query, limit); }
            catch { /* fall through to LIKE */ }
        }

        return HealthSearchLike(query, limit);
    }

    private List<HealthEntry> HealthVectorSearch(string query, int limit)
    {
        var queryEmbedding = _embeddings!.Embed(query);

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT date, summary, embedding FROM health_data
            WHERE embedding IS NOT NULL
            """;

        var scored = new List<(HealthEntry Entry, float Score)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var entry = new HealthEntry(reader.GetString(0), reader.GetString(1));
            var blob = (byte[])reader.GetValue(2);
            var embedding = EmbeddingService.Deserialize(blob);
            var score = EmbeddingService.Similarity(queryEmbedding, embedding);
            scored.Add((entry, score));
        }

        return scored
            .OrderByDescending(x => x.Score)
            .Take(limit)
            .Select(x => x.Entry)
            .ToList();
    }

    private List<HealthEntry> HealthSearchLike(string query, int limit)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT date, summary FROM health_data
            WHERE summary LIKE @pattern
            ORDER BY date DESC LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@pattern", $"%{query}%");
        cmd.Parameters.AddWithValue("@limit", limit);

        var entries = new List<HealthEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            entries.Add(new HealthEntry(reader.GetString(0), reader.GetString(1)));
        return entries;
    }

    // ── Embedding Backfill ──────────────────────────────────────

    /// <summary>
    /// Generate embeddings for any entries that don't have one yet.
    /// Call once at startup.
    /// </summary>
    public int BackfillEmbeddings()
    {
        if (_embeddings is not { IsAvailable: true }) return 0;

        using var selectCmd = _conn.CreateCommand();
        selectCmd.CommandText = "SELECT id, content, tags FROM entries WHERE embedding IS NULL";

        var toBackfill = new List<(int Id, string Text)>();
        using (var reader = selectCmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var id = reader.GetInt32(0);
                var content = reader.GetString(1);
                var tags = reader.IsDBNull(2) ? null : reader.GetString(2);
                var text = string.IsNullOrEmpty(tags) ? content : $"{content}\n{tags}";
                toBackfill.Add((id, text));
            }
        }

        if (toBackfill.Count == 0) return 0;

        Console.Error.WriteLine($"Backfilling embeddings for {toBackfill.Count} entries...");

        var count = 0;
        foreach (var (id, text) in toBackfill)
        {
            try
            {
                var emb = EmbeddingService.Serialize(_embeddings.Embed(text));
                using var updateCmd = _conn.CreateCommand();
                updateCmd.CommandText = "UPDATE entries SET embedding = @emb WHERE id = @id";
                updateCmd.Parameters.AddWithValue("@emb", emb);
                updateCmd.Parameters.AddWithValue("@id", id);
                updateCmd.ExecuteNonQuery();
                count++;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  Failed entry #{id}: {ex.Message}");
            }
        }

        Console.Error.WriteLine($"Backfilled {count}/{toBackfill.Count} entries.");
        return count;
    }

    // ── API Key Management ─────────────────────────────────────

    /// <summary>
    /// Create a new API key. Returns the raw key (shown once, never stored).
    /// </summary>
    public (string RawKey, int Id) CreateApiKey(string name)
    {
        var rawKey = $"recall_{GenerateRandomKey(32)}";
        var hash = HashKey(rawKey);

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO api_keys (name, key_hash, created_at)
            VALUES (@name, @hash, @now);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@hash", hash);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        var id = Convert.ToInt32(cmd.ExecuteScalar());

        return (rawKey, id);
    }

    /// <summary>
    /// Validate a raw API key. Returns true if valid and not revoked.
    /// Updates last_used timestamp on success.
    /// </summary>
    public bool ValidateApiKey(string rawKey)
    {
        var hash = HashKey(rawKey);

        using var checkCmd = _conn.CreateCommand();
        checkCmd.CommandText = "SELECT id FROM api_keys WHERE key_hash = @hash AND revoked = 0";
        checkCmd.Parameters.AddWithValue("@hash", hash);
        var result = checkCmd.ExecuteScalar();
        if (result is null) return false;

        // Update last_used
        using var updateCmd = _conn.CreateCommand();
        updateCmd.CommandText = "UPDATE api_keys SET last_used = @now WHERE id = @id";
        updateCmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        updateCmd.Parameters.AddWithValue("@id", result);
        updateCmd.ExecuteNonQuery();

        return true;
    }

    /// <summary>
    /// Revoke an API key by ID.
    /// </summary>
    public bool RevokeApiKey(int id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE api_keys SET revoked = 1 WHERE id = @id AND revoked = 0";
        cmd.Parameters.AddWithValue("@id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// List all API keys (without hashes).
    /// </summary>
    public List<ApiKeyInfo> ListApiKeys()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, created_at, last_used, revoked
            FROM api_keys ORDER BY id
            """;
        var keys = new List<ApiKeyInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            keys.Add(new ApiKeyInfo(
                Id: reader.GetInt32(0),
                Name: reader.GetString(1),
                CreatedAt: reader.GetString(2),
                LastUsed: reader.IsDBNull(3) ? null : reader.GetString(3),
                Revoked: reader.GetInt32(4) != 0));
        }
        return keys;
    }

    /// <summary>
    /// Returns true if there are any active (non-revoked) API keys.
    /// Used to determine if auth should be enforced.
    /// </summary>
    public bool HasApiKeys()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM api_keys WHERE revoked = 0";
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
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

    public void Dispose()
    {
        if (!_disposed)
        {
            _conn.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}

public record HealthEntry(string Date, string Summary);

public record ApiKeyInfo(
    int Id,
    string Name,
    string CreatedAt,
    string? LastUsed,
    bool Revoked);
