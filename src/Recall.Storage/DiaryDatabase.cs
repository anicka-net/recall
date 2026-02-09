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
    private bool _disposed;

    public DiaryDatabase(string dbPath)
    {
        _conn = Schema.CreateConnection(dbPath);
    }

    public int WriteEntry(string content, string? tags = null, string? conversationId = null,
        string source = "claude-code")
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO entries (created_at, content, tags, conversation_id, source)
            VALUES (@now, @content, @tags, @cid, @source);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@content", content);
        cmd.Parameters.AddWithValue("@tags", (object?)tags ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cid", (object?)conversationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@source", source);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public List<DiaryEntry> Search(string query, int limit = 10)
    {
        // Sanitize query for FTS5: remove special chars, wrap tokens
        var sanitized = SanitizeFtsQuery(query);
        if (string.IsNullOrWhiteSpace(sanitized))
            return GetRecent(limit);

        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT e.id, e.created_at, e.content, e.tags, e.conversation_id
                FROM entries e
                WHERE e.id IN (
                    SELECT rowid FROM entries_fts
                    WHERE entries_fts MATCH @query
                )
                ORDER BY e.created_at DESC
                LIMIT @limit
                """;
            cmd.Parameters.AddWithValue("@query", sanitized);
            cmd.Parameters.AddWithValue("@limit", limit);
            return ReadEntries(cmd);
        }
        catch
        {
            // FTS5 query failed - fall back to LIKE search
            return SearchLike(query, limit);
        }
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

    private static string SanitizeFtsQuery(string query)
    {
        // Remove FTS5 special characters that cause parse errors
        var cleaned = query
            .Replace("\"", " ")
            .Replace("'", " ")
            .Replace("(", " ")
            .Replace(")", " ")
            .Replace("*", " ")
            .Replace(":", " ")
            .Replace("?", " ")
            .Replace("!", " ")
            .Replace(".", " ")
            .Replace(",", " ");

        // Split into words, filter empty, rejoin (implicit AND in FTS5)
        var words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // Remove FTS5 operators that might appear as words
        var filtered = words.Where(w =>
            !w.Equals("AND", StringComparison.OrdinalIgnoreCase) &&
            !w.Equals("OR", StringComparison.OrdinalIgnoreCase) &&
            !w.Equals("NOT", StringComparison.OrdinalIgnoreCase) &&
            !w.Equals("NEAR", StringComparison.OrdinalIgnoreCase));

        return string.Join(" ", filtered);
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

public record ApiKeyInfo(
    int Id,
    string Name,
    string CreatedAt,
    string? LastUsed,
    bool Revoked);
