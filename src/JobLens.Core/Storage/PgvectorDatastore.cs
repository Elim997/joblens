using JobLens.Core.Parsing;
using Npgsql;
using Pgvector;

namespace JobLens.Core.Storage;

public class PgvectorDatastore(NpgsqlDataSource dataSource) : IDatastore
{
    public async Task EnsureSchemaAsync(int dimension, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        await using (var extensionCommand = connection.CreateCommand())
        {
            extensionCommand.CommandText = "CREATE EXTENSION IF NOT EXISTS vector;";
            await extensionCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var tableCommand = connection.CreateCommand())
        {
            // dimension comes from a live embedding call, not a hardcoded literal - see GeminiEmbedder.
            tableCommand.CommandText = $"""
                CREATE TABLE IF NOT EXISTS job_postings (
                    message_id TEXT PRIMARY KEY,
                    title TEXT NOT NULL,
                    company TEXT NOT NULL,
                    location TEXT NOT NULL,
                    category TEXT NOT NULL,
                    apply_url TEXT NOT NULL,
                    description TEXT NOT NULL,
                    embedding vector({dimension}) NOT NULL,
                    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
                    scored_at TIMESTAMPTZ NULL,
                    score INT NULL,
                    reasoning TEXT NULL,
                    selected_template TEXT NULL
                );
                """;
            await tableCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        // Migration path for a table created before Milestone 6/multi-template scoring:
        // CREATE TABLE above won't touch an existing table, so add the columns separately,
        // idempotently. Rows scored before this migration keep selected_template NULL -
        // they are never rescored automatically just to backfill it.
        await using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = """
            ALTER TABLE job_postings
                ADD COLUMN IF NOT EXISTS scored_at TIMESTAMPTZ NULL,
                ADD COLUMN IF NOT EXISTS score INT NULL,
                ADD COLUMN IF NOT EXISTS reasoning TEXT NULL,
                ADD COLUMN IF NOT EXISTS selected_template TEXT NULL;
            """;
        await alterCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlySet<string>> GetExistingMessageIdsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        await using (var existsCommand = connection.CreateCommand())
        {
            existsCommand.CommandText = "SELECT to_regclass('public.job_postings') IS NOT NULL;";
            var tableExists = (bool)(await existsCommand.ExecuteScalarAsync(cancellationToken))!;
            if (!tableExists)
                return new HashSet<string>();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT message_id FROM job_postings;";

        var ids = new HashSet<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            ids.Add(reader.GetString(0));

        return ids;
    }

    public async Task UpsertAsync(string messageId, JobPosting posting, float[] embedding, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // message_id is the primary key, so a duplicate is already rejected at the DB
        // level - ON CONFLICT DO NOTHING makes a re-ingest of a known message a clean
        // no-op instead of relying on the caller to pre-filter (Program.cs already does,
        // via GetExistingMessageIdsAsync, but this is the guard for a concurrent race).
        command.CommandText = """
            INSERT INTO job_postings (message_id, title, company, location, category, apply_url, description, embedding)
            VALUES (@messageId, @title, @company, @location, @category, @applyUrl, @description, @embedding)
            ON CONFLICT (message_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("messageId", messageId);
        command.Parameters.AddWithValue("title", posting.Title);
        command.Parameters.AddWithValue("company", posting.Company);
        command.Parameters.AddWithValue("location", posting.Location);
        command.Parameters.AddWithValue("category", posting.Category);
        command.Parameters.AddWithValue("applyUrl", posting.ApplyUrl);
        command.Parameters.AddWithValue("description", posting.Description);
        command.Parameters.AddWithValue("embedding", new Vector(embedding));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<JobPosting?> GetPostingByMessageIdAsync(string messageId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT title, company, location, category, apply_url, description
            FROM job_postings
            WHERE message_id = @messageId;
            """;
        command.Parameters.AddWithValue("messageId", messageId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new JobPosting(
            Title: reader.GetString(0),
            Company: reader.GetString(1),
            Location: reader.GetString(2),
            Category: reader.GetString(3),
            ApplyUrl: reader.GetString(4),
            Description: reader.GetString(5));
    }

    public async Task<IReadOnlyList<SimilarPosting>> QuerySimilarAsync(float[] queryEmbedding, int topK, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // <=> is pgvector's cosine distance; similarity = 1 - distance. Must stay cosine
        // (vector_cosine_ops) if an ANN index is added later, to match this operator.
        command.CommandText = """
            SELECT title, company, location, category, apply_url, description,
                   1 - (embedding <=> @queryEmbedding) AS similarity
            FROM job_postings
            ORDER BY embedding <=> @queryEmbedding
            LIMIT @topK;
            """;
        command.Parameters.AddWithValue("queryEmbedding", new Vector(queryEmbedding));
        command.Parameters.AddWithValue("topK", topK);

        var results = new List<SimilarPosting>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var posting = new JobPosting(
                Title: reader.GetString(0),
                Company: reader.GetString(1),
                Location: reader.GetString(2),
                Category: reader.GetString(3),
                ApplyUrl: reader.GetString(4),
                Description: reader.GetString(5));
            results.Add(new SimilarPosting(posting, reader.GetDouble(6)));
        }

        return results;
    }

    public async Task<IReadOnlyList<UnscoredPosting>> GetUnscoredPostingsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT message_id, title, company, location, category, apply_url, description, embedding
            FROM job_postings
            WHERE scored_at IS NULL;
            """;

        var results = new List<UnscoredPosting>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var posting = new JobPosting(
                Title: reader.GetString(1),
                Company: reader.GetString(2),
                Location: reader.GetString(3),
                Category: reader.GetString(4),
                ApplyUrl: reader.GetString(5),
                Description: reader.GetString(6));
            var embedding = reader.GetFieldValue<Vector>(7).ToArray();
            results.Add(new UnscoredPosting(reader.GetString(0), posting, embedding));
        }

        return results;
    }

    public async Task MarkScoredAsync(IReadOnlyList<ScoredMark> scored, CancellationToken cancellationToken = default)
    {
        if (scored.Count == 0)
            return;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // UNNEST zips the four arrays into rows, so each message_id gets its own
        // score/reasoning/selected_template in a single round trip instead of one UPDATE
        // per posting.
        command.CommandText = """
            UPDATE job_postings AS jp
            SET scored_at = now(), score = data.score, reasoning = data.reasoning, selected_template = data.selected_template
            FROM UNNEST(@ids, @scores, @reasonings, @templateNames) AS data(message_id, score, reasoning, selected_template)
            WHERE jp.message_id = data.message_id;
            """;
        command.Parameters.AddWithValue("ids", scored.Select(s => s.MessageId).ToArray());
        command.Parameters.AddWithValue("scores", scored.Select(s => s.Score).ToArray());
        command.Parameters.AddWithValue("reasonings", scored.Select(s => s.Reasoning).ToArray());
        command.Parameters.AddWithValue("templateNames", scored.Select(s => s.TemplateName).ToArray());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<StoredMatch>> GetMatchesAsync(int matchThreshold, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT message_id, title, company, location, category, apply_url, description, score, reasoning, selected_template
            FROM job_postings
            WHERE score >= @matchThreshold
            ORDER BY score DESC;
            """;
        command.Parameters.AddWithValue("matchThreshold", matchThreshold);

        var results = new List<StoredMatch>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var messageId = reader.GetString(0);
            var posting = new JobPosting(
                Title: reader.GetString(1),
                Company: reader.GetString(2),
                Location: reader.GetString(3),
                Category: reader.GetString(4),
                ApplyUrl: reader.GetString(5),
                Description: reader.GetString(6));
            // selected_template is NULL for rows scored before this migration - never
            // backfilled automatically, so this read must stay null-safe.
            var templateName = reader.IsDBNull(9) ? null : reader.GetString(9);
            results.Add(new StoredMatch(messageId, posting, reader.GetInt32(7), reader.GetString(8), templateName));
        }

        // The feed sometimes reposts the same job under a different message_id; collapse
        // here rather than in SQL so the query stays simple and dedup logic is shared
        // with PipelineRunner's notify path. Rows are already ordered by score DESC, so
        // the highest-scored copy of a duplicate is the one that survives.
        return NearDuplicateCollapser.Collapse(results, m => m.Posting);
    }
}
