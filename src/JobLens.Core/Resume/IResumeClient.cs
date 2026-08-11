using System.Text.Json.Nodes;

namespace JobLens.Core.Resume;

public record ResumeSummary(string Id, string Name, string? JobTitle, string CreatedAt, string UpdatedAt);

public interface IResumeClient
{
    Task<IReadOnlyList<ResumeSummary>> ListResumesAsync(CancellationToken cancellationToken = default);

    Task<JsonNode?> ReadResumeAsync(string resumeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deep-merges <paramref name="resume"/> onto the existing resume at <paramref name="resumeId"/>.
    /// Rezi's write_resume applies a partial merge, not a replace - reuse item IDs from a prior
    /// ReadResumeAsync to edit existing content; a new key adds an item; null deletes one.
    /// </summary>
    Task<JsonNode?> WriteResumeAsync(string resumeId, JsonNode resume, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rezi's resume section/field schema: what each section means and the valid content
    /// fields for it. Read before constructing a WriteResumeAsync payload.
    /// </summary>
    Task<JsonNode?> GetResumeFormatAsync(CancellationToken cancellationToken = default);
}
