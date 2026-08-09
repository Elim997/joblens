namespace JobLens.Core.Feed;

public interface IJobFeedSource
{
    Task<IReadOnlyList<RawMessage>> GetMessagesAsync(CancellationToken cancellationToken = default);
}
