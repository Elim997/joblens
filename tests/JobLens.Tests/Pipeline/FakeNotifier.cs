using JobLens.Core.Notification;
using JobLens.Core.Scoring;

namespace JobLens.Tests.Pipeline;

public class FakeNotifier : INotifier
{
    public List<IReadOnlyList<ScoredPosting>> Calls { get; } = [];

    public Task NotifyAsync(IReadOnlyList<ScoredPosting> matches, CancellationToken cancellationToken = default)
    {
        Calls.Add(matches);
        return Task.CompletedTask;
    }
}
