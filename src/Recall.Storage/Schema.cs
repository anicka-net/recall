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

        -- API keys for HTTP transport authentication
        CREATE TABLE IF NOT EXISTS api_keys (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT NOT NULL,
            key_hash TEXT NOT NULL UNIQUE,
            created_at TEXT NOT NULL,
            last_used TEXT,
            revoked INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS idx_api_keys_hash
            ON api_keys(key_hash) WHERE revoked = 0;
        """;

    public static void Initialize(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = CreateTablesSql;
        cmd.ExecuteNonQuery();

        Migrate(connection);
    }

    private static void Migrate(SqliteConnection connection)
    {
        using var verCmd = connection.CreateCommand();
        verCmd.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(verCmd.ExecuteScalar());

        if (version < 1)
        {
            // Add embedding column for vector search
            using var alter = connection.CreateCommand();
            alter.CommandText = """
                ALTER TABLE entries ADD COLUMN embedding BLOB;
                PRAGMA user_version = 1;
                """;
            alter.ExecuteNonQuery();
        }

        if (version < 2)
        {
            // Add health_data table for Fitbit health metrics
            using var health = connection.CreateCommand();
            health.CommandText = """
                CREATE TABLE IF NOT EXISTS health_data (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    date TEXT NOT NULL UNIQUE,
                    summary TEXT NOT NULL,
                    sleep_json TEXT,
                    heart_json TEXT,
                    activity_json TEXT,
                    spo2_json TEXT,
                    embedding BLOB,
                    synced_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_health_date ON health_data(date DESC);
                PRAGMA user_version = 2;
                """;
            health.ExecuteNonQuery();
        }

        if (version < 3)
        {
            // OAuth 2.1 tables for Claude.ai authentication
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

        if (version < 4)
        {
            // Privilege separation: restricted entries only visible with secret
            using var restrict = connection.CreateCommand();
            restrict.CommandText = """
                ALTER TABLE entries ADD COLUMN restricted INTEGER NOT NULL DEFAULT 0;
                PRAGMA user_version = 4;
                """;
            restrict.ExecuteNonQuery();
        }

        if (version < 5)
        {
            // Scoped users: isolated diary spaces per project/user
            using var scope = connection.CreateCommand();
            scope.CommandText = """
                ALTER TABLE entries ADD COLUMN scope TEXT;
                CREATE INDEX IF NOT EXISTS idx_entries_scope ON entries(scope) WHERE scope IS NOT NULL;
                PRAGMA user_version = 5;
                """;
            scope.ExecuteNonQuery();
        }
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
