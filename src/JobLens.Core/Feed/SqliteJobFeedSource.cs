using System.Globalization;
using JobLens.Core.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace JobLens.Core.Feed;

public class SqliteJobFeedSource(IOptions<JobLensOptions> options) : IJobFeedSource
{
    private readonly JobLensOptions _options = options.Value;

    public async Task<IReadOnlyList<RawMessage>> GetMessagesAsync(CancellationToken cancellationToken = default)
    {
        // Read-only: the WhatsApp bridge process owns this file and writes to it
        // continuously. We only ever read from it.
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _options.MessagesDbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        // Placeholder names are generated from the array index, not user input, so
        // interpolating them into the IN(...) list is safe; values are parameterized.
        var placeholders = _options.GroupChatJids.Select((_, i) => $"@chatJid{i}");
        command.CommandText = $"""
            SELECT id, chat_jid, sender, content, timestamp
            FROM messages
            WHERE chat_jid IN ({string.Join(", ", placeholders)})
              AND content IS NOT NULL AND content <> ''
              AND (media_type IS NULL OR media_type = '')
            ORDER BY timestamp ASC
            """;
        for (var i = 0; i < _options.GroupChatJids.Length; i++)
            command.Parameters.AddWithValue($"@chatJid{i}", _options.GroupChatJids[i]);

        var messages = new List<RawMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(new RawMessage(
                Id: reader.GetString(0),
                ChatJid: reader.GetString(1),
                Sender: reader.GetString(2),
                Content: reader.GetString(3),
                Timestamp: DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture)));
        }

        return messages;
    }
}
