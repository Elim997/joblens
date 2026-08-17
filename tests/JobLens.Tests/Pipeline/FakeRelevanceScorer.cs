using JobLens.Core.Parsing;
using JobLens.Core.Scoring;

namespace JobLens.Tests.Pipeline;

public class FakeRelevanceScorer(
    Func<IReadOnlyList<(string Id, JobPosting Posting, float[] Embedding)>, IReadOnlyList<ScoredPosting>> respond)
    : IRelevanceScorer
{
    public List<IReadOnlyList<(string Id, JobPosting Posting, float[] Embedding)>> Calls { get; } = [];

    public Task<IReadOnlyList<ScoredPosting>> ScoreAsync(
        IReadOnlyList<(string Id, JobPosting Posting, float[] Embedding)> candidates,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(candidates);
        return Task.FromResult(respond(candidates));
    }
}
