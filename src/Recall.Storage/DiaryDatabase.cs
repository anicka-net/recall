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
