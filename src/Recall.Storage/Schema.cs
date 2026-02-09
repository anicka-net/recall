using Microsoft.Data.Sqlite;

namespace Recall.Storage;

public static class Schema
{
    private const string CreateTablesSql = """
        PRAGMA journal_mode = 'wal';
        PRAGMA busy_timeout = 5000;

        CREATE TABLE IF NOT EXISTS entries (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            created_at TEXT NOT NULL,
            content TEXT NOT NULL,
            tags TEXT,
            conversation_id TEXT,
            source TEXT DEFAULT 'claude-code'
        );

        CREATE INDEX IF NOT EXISTS idx_entries_created
            ON entries(created_at DESC);
        CREATE INDEX IF NOT EXISTS idx_entries_conversation
            ON entries(conversation_id);

        CREATE VIRTUAL TABLE IF NOT EXISTS entries_fts USING fts5(
            content,
            tags,
            content=entries,
            content_rowid=id
        );

        CREATE TRIGGER IF NOT EXISTS entries_ai AFTER INSERT ON entries BEGIN
            INSERT INTO entries_fts(rowid, content, tags)
            VALUES (new.id, new.content, new.tags);
        END;

        CREATE TRIGGER IF NOT EXISTS entries_ad AFTER DELETE ON entries BEGIN
            INSERT INTO entries_fts(entries_fts, rowid, content, tags)
            VALUES ('delete', old.id, old.content, old.tags);
        END;

        CREATE TRIGGER IF NOT EXISTS entries_au AFTER UPDATE ON entries BEGIN
            INSERT INTO entries_fts(entries_fts, rowid, content, tags)
            VALUES ('delete', old.id, old.content, old.tags);
            INSERT INTO entries_fts(rowid, content, tags)
            VALUES (new.id, new.content, new.tags);
        END;
        """;

    public static void Initialize(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = CreateTablesSql;
        cmd.ExecuteNonQuery();
    }

    public static SqliteConnection CreateConnection(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

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
