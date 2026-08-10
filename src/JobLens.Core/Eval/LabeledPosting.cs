using System.Text.Json;
using JobLens.Core.Parsing;

namespace JobLens.Core.Eval;

public record LabeledPosting(string MessageId, JobPosting Posting, bool IsRelevant);

/// <summary>
/// A labeled set as loaded from disk: the postings plus the caveat that travels with
/// them (see labeled-postings.json), so EvalReport can surface it without a second
/// source of truth to keep in sync.
/// </summary>
public record LabeledPostingSet(string Caveat, IReadOnlyList<LabeledPosting> Postings);

public static class LabeledPostingLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private record FileRow(
        string MessageId, string Title, string Company, string Location,
        string Category, string ApplyUrl, string Description, bool IsRelevant);

    private record FileContents(string Caveat, IReadOnlyList<FileRow> Postings);

    public static async Task<LabeledPostingSet> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var contents = await JsonSerializer.DeserializeAsync<FileContents>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException($"Could not parse labeled postings file: {path}");

        var postings = contents.Postings
            .Select(r => new LabeledPosting(
                r.MessageId,
                new JobPosting(r.Title, r.Company, r.Location, r.Category, r.ApplyUrl, r.Description),
                r.IsRelevant))
            .ToList();

        return new LabeledPostingSet(contents.Caveat, postings);
    }
}
