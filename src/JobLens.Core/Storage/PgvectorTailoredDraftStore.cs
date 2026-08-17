using System.Text.Json;
using JobLens.Core.Resume;
using Npgsql;
using NpgsqlTypes;

namespace JobLens.Core.Storage;

/// <summary>
/// No FK to job_postings, matching this codebase's existing no-FK convention - the app layer
/// (TailoredDraftService) already guarantees the posting exists and is scored before a draft is
/// built. Every method bootstraps its own table (CREATE TABLE IF NOT EXISTS, cheap and
/// idempotent) rather than relying on a caller-driven EnsureSchemaAsync like PgvectorDatastore,
/// since this table has no dimension-dependent DDL and no caller-ordering requirement.
/// </summary>
public class PgvectorTailoredDraftStore(NpgsqlDataSource dataSource) : ITailoredDraftStore
{
    private const string SelectColumns = """
        id, message_id, score, selected_template, base_resume_id, base_resume_name,
        summary, experience, skills, rewrite_rationale, status, created_at, exported_at
        """;

    public async Task<TailoredDraft> CreateOrGetAsync(NewTailoredDraft draft, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await EnsureTableAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        // Single round trip, race-safe: a plain "ON CONFLICT DO NOTHING RETURNING ..." plus a
        // fallback SELECT (even unioned in the same CTE) is NOT safe here - in READ COMMITTED,
        // both branches of that CTE share one snapshot taken at statement start, so when this
        // insert blocks on a concurrent conflicting insert and then loses the race, the fallback
        // SELECT still can't see the row the other session just committed, and the whole
        // statement can return zero rows. A self-referential no-op DO UPDATE sidesteps that: it
        // always has exactly one branch, PostgreSQL locks the conflicting row and evaluates the
        // UPDATE/RETURNING against its current committed content (not the original snapshot), and
        // it never actually mutates an existing row's content (draft content is immutable after
        // creation - only MarkExportedAsync may change status/exported_at).
        command.CommandText = $"""
            INSERT INTO tailored_drafts
                (id, message_id, score, selected_template, base_resume_id, base_resume_name,
                 summary, experience, skills, rewrite_rationale)
            VALUES
                (@id, @messageId, @score, @selectedTemplate, @baseResumeId, @baseResumeName,
                 @summary, @experience, @skills, @rewriteRationale)
            ON CONFLICT (message_id, selected_template, base_resume_id)
            DO UPDATE SET id = tailored_drafts.id
            RETURNING {SelectColumns};
            """;
        command.Parameters.AddWithValue("id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("messageId", draft.MessageId);
        command.Parameters.AddWithValue("score", draft.Score);
        command.Parameters.AddWithValue("selectedTemplate", draft.SelectedTemplate);
        command.Parameters.AddWithValue("baseResumeId", draft.BaseResumeId);
        command.Parameters.AddWithValue("baseResumeName", draft.BaseResumeName);
        command.Parameters.AddWithValue("summary", draft.Summary);
        command.Parameters.Add(new NpgsqlParameter("experience", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(draft.Experience) });
        command.Parameters.Add(new NpgsqlParameter("skills", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(draft.Skills) });
        command.Parameters.AddWithValue("rewriteRationale", draft.RewriteRationale);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken); // exactly one row: the new insert or the pre-existing one
        return MapRow(reader);
    }

    public async Task<TailoredDraft?> FindAsync(string messageId, string selectedTemplate, string baseResumeId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await EnsureTableAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM tailored_drafts
            WHERE message_id = @messageId AND selected_template = @selectedTemplate AND base_resume_id = @baseResumeId;
            """;
        command.Parameters.AddWithValue("messageId", messageId);
        command.Parameters.AddWithValue("selectedTemplate", selectedTemplate);
        command.Parameters.AddWithValue("baseResumeId", baseResumeId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapRow(reader) : null;
    }

    public async Task<IReadOnlyList<TailoredDraft>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await EnsureTableAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM tailored_drafts ORDER BY created_at DESC;";

        var results = new List<TailoredDraft>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(MapRow(reader));

        return results;
    }

    public async Task<TailoredDraft?> GetByIdAsync(string draftId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await EnsureTableAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM tailored_drafts WHERE id = @id;";
        command.Parameters.AddWithValue("id", draftId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapRow(reader) : null;
    }

    public async Task<TailoredDraft?> MarkExportedAsync(string draftId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await EnsureTableAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        // Re-exporting the same draft is safe by design (see TailoredDraftExporter) - this just
        // refreshes exported_at rather than rejecting an already-exported row.
        command.CommandText = $"""
            UPDATE tailored_drafts
            SET status = 'ExportedToRezi', exported_at = now()
            WHERE id = @id
            RETURNING {SelectColumns};
            """;
        command.Parameters.AddWithValue("id", draftId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapRow(reader) : null;
    }

    private static async Task EnsureTableAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS tailored_drafts (
                id TEXT PRIMARY KEY,
                message_id TEXT NOT NULL,
                score INT NOT NULL,
                selected_template TEXT NOT NULL,
                base_resume_id TEXT NOT NULL,
                base_resume_name TEXT NOT NULL,
                summary TEXT NOT NULL,
                experience JSONB NOT NULL,
                skills JSONB NOT NULL,
                rewrite_rationale TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT 'Draft',
                created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
                exported_at TIMESTAMPTZ NULL,
                UNIQUE (message_id, selected_template, base_resume_id)
            );
            """;
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // CREATE TABLE IF NOT EXISTS is not atomic across concurrent sessions: when the table
            // doesn't exist yet, two sessions can both see "missing" and both attempt to create it,
            // colliding on the implicit pg_type row for the table (a documented PostgreSQL DDL
            // race, not an application bug). A unique-violation here means a concurrent session won
            // that race and the table now exists - exactly the outcome IF NOT EXISTS asked for - so
            // it is safe to treat the same as "already existed" rather than surface as a failure.
        }
    }

    private static TailoredDraft MapRow(NpgsqlDataReader reader) =>
        new(
            Id: reader.GetString(0),
            MessageId: reader.GetString(1),
            Score: reader.GetInt32(2),
            SelectedTemplate: reader.GetString(3),
            BaseResumeId: reader.GetString(4),
            BaseResumeName: reader.GetString(5),
            Summary: reader.GetString(6),
            Experience: JsonSerializer.Deserialize<List<TailoredExperienceItem>>(reader.GetString(7))!,
            Skills: JsonSerializer.Deserialize<List<TailoredSkillItem>>(reader.GetString(8))!,
            RewriteRationale: reader.GetString(9),
            Status: Enum.Parse<TailoredDraftStatus>(reader.GetString(10)),
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(11),
            ExportedAt: reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12));
}
