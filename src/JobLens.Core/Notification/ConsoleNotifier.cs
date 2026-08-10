using JobLens.Core.Scoring;
using Microsoft.Extensions.Logging;

namespace JobLens.Core.Notification;

public class ConsoleNotifier(ILogger<ConsoleNotifier> logger) : INotifier
{
    public Task NotifyAsync(IReadOnlyList<ScoredPosting> matches, CancellationToken cancellationToken = default)
    {
        foreach (var match in matches)
        {
            logger.LogInformation(
                "Match ({Score}): {Title} at {Company} - {Reasoning} ({ApplyUrl})",
                match.Score, match.Posting.Title, match.Posting.Company, match.Reasoning, match.Posting.ApplyUrl);
        }

        return Task.CompletedTask;
    }
}
