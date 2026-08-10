using JobLens.Core.Scoring;

namespace JobLens.Core.Notification;

public interface INotifier
{
    Task NotifyAsync(IReadOnlyList<ScoredPosting> matches, CancellationToken cancellationToken = default);
}
