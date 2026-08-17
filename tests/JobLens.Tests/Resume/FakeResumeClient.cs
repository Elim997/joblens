using System.Text.Json.Nodes;
using JobLens.Core.Resume;

namespace JobLens.Tests.Resume;

/// <summary>
/// In-memory IResumeClient for tests. Seed resumes with Seed(); WriteResumeAsync deep-merges
/// onto the seeded JSON the same way Rezi's real write_resume does, so callers can assert on
/// merge behavior (existing item IDs edited in place, new keys added) without a live connection.
/// </summary>
public class FakeResumeClient : IResumeClient
{
    private readonly Dictionary<string, JsonNode> _resumes = [];
    private readonly List<string> _reads = [];
    private readonly List<(string ResumeId, JsonNode Resume)> _writes = [];

    public IReadOnlyList<string> Reads => _reads;

    public IReadOnlyList<(string ResumeId, JsonNode Resume)> Writes => _writes;

    public Exception? WriteException { get; set; }

    public void Seed(string resumeId, JsonNode resume) => _resumes[resumeId] = resume;

    public Task<IReadOnlyList<ResumeSummary>> ListResumesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ResumeSummary>>(
            _resumes.Select(kvp => new ResumeSummary(
                kvp.Key,
                kvp.Value["name"]?.GetValue<string>() ?? "",
                kvp.Value["jobTitle"]?.GetValue<string>(),
                kvp.Value["createdAt"]?.GetValue<string>() ?? "",
                kvp.Value["updatedAt"]?.GetValue<string>() ?? ""))
            .ToList());

    public Task<JsonNode?> ReadResumeAsync(string resumeId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _reads.Add(resumeId);
        return Task.FromResult(_resumes.TryGetValue(resumeId, out var resume) ? resume.DeepClone() : null);
    }

    public Task<JsonNode?> WriteResumeAsync(string resumeId, JsonNode resume, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (WriteException is not null)
            throw WriteException;

        _writes.Add((resumeId, resume.DeepClone()));

        var existing = _resumes.TryGetValue(resumeId, out var current) ? current : new JsonObject();
        var merged = DeepMerge(existing, resume);
        _resumes[resumeId] = merged;
        return Task.FromResult<JsonNode?>(merged.DeepClone());
    }

    public Task<JsonNode?> GetResumeFormatAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<JsonNode?>(new JsonObject { ["sections"] = new JsonArray() });

    private static JsonNode DeepMerge(JsonNode target, JsonNode patch)
    {
        if (target is not JsonObject targetObj || patch is not JsonObject patchObj)
            return patch.DeepClone();

        var result = targetObj.DeepClone().AsObject();
        foreach (var (key, value) in patchObj)
        {
            if (value is null)
            {
                result.Remove(key);
            }
            else if (result.TryGetPropertyValue(key, out var existingValue) && existingValue is not null)
            {
                result[key] = DeepMerge(existingValue, value);
            }
            else
            {
                result[key] = value.DeepClone();
            }
        }

        return result;
    }
}
