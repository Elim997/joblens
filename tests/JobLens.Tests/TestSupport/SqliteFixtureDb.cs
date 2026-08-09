using Microsoft.Data.Sqlite;

namespace JobLens.Tests.TestSupport;

/// <summary>
/// A throwaway SQLite file, schema-matched to the real WhatsApp bridge's
/// messages.db, for hermetic IJobFeedSource tests.
/// </summary>
public sealed class SqliteFixtureDb : IDisposable
{
    public string Path { get; }

    public SqliteFixtureDb()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"joblens-test-{Guid.NewGuid():N}.db");

        using var connection = new SqliteConnection($"Data Source={Path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE chats (
                jid TEXT PRIMARY KEY,
                name TEXT,
                last_message_time TIMESTAMP
            );
            CREATE TABLE messages (
                id TEXT,
                chat_jid TEXT,
                sender TEXT,
                content TEXT,
                timestamp TIMESTAMP,
                is_from_me BOOLEAN,
                media_type TEXT,
                filename TEXT,
                url TEXT,
                media_key BLOB,
                file_sha256 BLOB,
                file_enc_sha256 BLOB,
                file_length INTEGER,
                PRIMARY KEY (id, chat_jid),
                FOREIGN KEY (chat_jid) REFERENCES chats(jid)
            );
            """;
        command.ExecuteNonQuery();
    }

    public void InsertMessage(string id, string chatJid, string sender, string? content, string timestamp, string? mediaType)
    {
        using var connection = new SqliteConnection($"Data Source={Path}");
        connection.Open();

        // messages.chat_jid has a FK to chats.jid; ensure the parent row exists.
        using (var chatCommand = connection.CreateCommand())
        {
            chatCommand.CommandText = "INSERT OR IGNORE INTO chats (jid, name) VALUES (@jid, @jid)";
            chatCommand.Parameters.AddWithValue("@jid", chatJid);
            chatCommand.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO messages (id, chat_jid, sender, content, timestamp, is_from_me, media_type)
            VALUES (@id, @chatJid, @sender, @content, @timestamp, 0, @mediaType)
            """;
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@chatJid", chatJid);
        command.Parameters.AddWithValue("@sender", sender);
        command.Parameters.AddWithValue("@content", (object?)content ?? DBNull.Value);
        command.Parameters.AddWithValue("@timestamp", timestamp);
        command.Parameters.AddWithValue("@mediaType", (object?)mediaType ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(Path))
            File.Delete(Path);
    }
}
