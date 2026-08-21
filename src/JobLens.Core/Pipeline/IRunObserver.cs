using JobLens.Core.Scoring;

namespace JobLens.Core.Pipeline;

public record ScheduledMatchDetail(
    string MessageId,
    string Title,
    string Company,
    int Score,
    string? SelectedTemplate,
    string DraftOutcome,
    string? DraftId);

public record ScoringTransportFailure(
    string TemplateName,
    string ExceptionType,
    string Message);

public static class DraftOutcomes
{
    public const string Created = "Created";
    public const string Reused = "Reused";
    public const string Failed = "Failed";
    public const string SkippedAuth = "SkippedAuth";
    public const string NoTemplate = "NoTemplate";
    public const string NotAttempted = "NotAttempted";
}

/// <summary>
/// Passive, per-invocation reporting for details that are transient during a pipeline run.
/// Implementations must not make pipeline policy decisions.
/// </summary>
public interface IRunObserver
{
    void MatchNotified(ScoredPosting match);

    void DraftResolved(
        string messageId,
        string? selectedTemplate,
        string draftOutcome,
        string? draftId = null);

    void ReziAuthenticationFailed(string message);

    void ScoringTransportFailed(
        string templateName,
        Exception exception);
}

public sealed class RunDetailCollector : IRunObserver
{
    private readonly object _sync = new();
    private readonly List<ScheduledMatchDetail> _matches = [];
    private readonly Dictionary<string, int> _matchIndexes =
        new(StringComparer.Ordinal);
    private readonly List<ScoringTransportFailure> _scoringTransportFailures = [];
    private string? _reziAuthenticationError;

    public IReadOnlyList<ScheduledMatchDetail> Matches
    {
        get
        {
            lock (_sync)
                return _matches.ToList();
        }
    }

    public string? ReziAuthenticationError
    {
        get
        {
            lock (_sync)
                return _reziAuthenticationError;
        }
    }

    public IReadOnlyList<ScoringTransportFailure> ScoringTransportFailures
    {
        get
        {
            lock (_sync)
                return _scoringTransportFailures.ToList();
        }
    }

    public void MatchNotified(ScoredPosting match)
    {
        lock (_sync)
        {
            if (_matchIndexes.ContainsKey(match.Id))
                return;

            _matchIndexes.Add(match.Id, _matches.Count);
            _matches.Add(new ScheduledMatchDetail(
                match.Id,
                match.Posting.Title,
                match.Posting.Company,
                match.Score,
                match.TemplateName,
                DraftOutcomes.NotAttempted,
                DraftId: null));
        }
    }

    public void DraftResolved(
        string messageId,
        string? selectedTemplate,
        string draftOutcome,
        string? draftId = null)
    {
        lock (_sync)
        {
            if (!_matchIndexes.TryGetValue(messageId, out var index))
                return;

            _matches[index] = _matches[index] with
            {
                SelectedTemplate = selectedTemplate,
                DraftOutcome = draftOutcome,
                DraftId = draftId,
            };
        }
    }

    public void ReziAuthenticationFailed(string message)
    {
        lock (_sync)
            _reziAuthenticationError ??= message;
    }

    public void ScoringTransportFailed(
        string templateName,
        Exception exception)
    {
        lock (_sync)
        {
            _scoringTransportFailures.Add(new ScoringTransportFailure(
                templateName,
                exception.GetType().Name,
                exception.Message));
        }
    }
}

internal sealed class NullRunObserver : IRunObserver
{
    public static NullRunObserver Instance { get; } = new();

    private NullRunObserver()
    {
    }

    public void MatchNotified(ScoredPosting match)
    {
    }

    public void DraftResolved(
        string messageId,
        string? selectedTemplate,
        string draftOutcome,
        string? draftId = null)
    {
    }

    public void ReziAuthenticationFailed(string message)
    {
    }

    public void ScoringTransportFailed(
        string templateName,
        Exception exception)
    {
    }
}
