using JobLens.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace JobLens.Core.Pipeline;

public enum ScheduledRunStatus
{
    Success,
    Fatal,
    LockUnavailable,
    Degraded,
    Cancelled,
}

public record ScheduledRunResult(
    ScheduledRunStatus Status,
    PreflightResult? Preflight,
    IngestSummary? Ingest,
    RunSummary? Pipeline,
    IReadOnlyList<ScheduledMatchDetail> Matches,
    string? ReziAuthenticationError,
    IReadOnlyList<ScoringTransportFailure> ScoringTransportFailures,
    string? FatalError = null)
{
    public int ExitCode => Status switch
    {
        ScheduledRunStatus.Success => 0,
        ScheduledRunStatus.Fatal => 1,
        ScheduledRunStatus.LockUnavailable => 2,
        ScheduledRunStatus.Degraded => 3,
        ScheduledRunStatus.Cancelled => 4,
        _ => throw new InvalidOperationException($"Unknown scheduled-run status '{Status}'."),
    };
}

/// <summary>
/// Owns one exclusive pipeline lock across the complete one-shot operation, then coordinates the
/// existing preflight, ingest-only, and backlog-drain services without duplicating their policies.
/// </summary>
public sealed class ScheduledRunService
{
    private readonly RunLock _runLock;
    private readonly IPreflight _preflight;
    private readonly Func<CancellationToken, Task<IngestSummary>> _ingestAsync;
    private readonly Func<IRunObserver, CancellationToken, Task<RunSummary>> _runPipelineAsync;
    private readonly ILogger<ScheduledRunService> _logger;

    public ScheduledRunService(
        RunLock runLock,
        IPreflight preflight,
        IngestService ingestService,
        PipelineRunner pipelineRunner,
        ILogger<ScheduledRunService> logger)
        : this(
            runLock,
            preflight,
            ingestService.IngestAsync,
            pipelineRunner.RunAsync,
            logger)
    {
    }

    internal ScheduledRunService(
        RunLock runLock,
        IPreflight preflight,
        Func<CancellationToken, Task<IngestSummary>> ingestAsync,
        Func<IRunObserver, CancellationToken, Task<RunSummary>> runPipelineAsync,
        ILogger<ScheduledRunService> logger)
    {
        _runLock = runLock;
        _preflight = preflight;
        _ingestAsync = ingestAsync;
        _runPipelineAsync = runPipelineAsync;
        _logger = logger;
    }

    public async Task<ScheduledRunResult> RunAsync(
        CancellationToken cancellationToken = default)
    {
        PreflightResult? preflightResult = null;
        IngestSummary? ingestSummary = null;
        RunSummary? pipelineSummary = null;
        var collector = new RunDetailCollector();

        try
        {
            using var runLockHandle = _runLock.TryAcquire();
            if (runLockHandle is null)
            {
                var unavailable = CreateResult(
                    ScheduledRunStatus.LockUnavailable,
                    preflightResult,
                    ingestSummary,
                    pipelineSummary,
                    collector);
                Report(unavailable);
                return unavailable;
            }

            preflightResult = await _preflight.RunAsync(cancellationToken);
            if (preflightResult.Status == PreflightStatus.Fatal)
            {
                var fatal = CreateResult(
                    ScheduledRunStatus.Fatal,
                    preflightResult,
                    ingestSummary,
                    pipelineSummary,
                    collector,
                    "Preflight reported a fatal environment failure.");
                Report(fatal);
                return fatal;
            }

            if (preflightResult.Status == PreflightStatus.Cancelled)
            {
                var cancelled = CreateResult(
                    ScheduledRunStatus.Cancelled,
                    preflightResult,
                    ingestSummary,
                    pipelineSummary,
                    collector);
                Report(cancelled);
                return cancelled;
            }

            ingestSummary = await _ingestAsync(cancellationToken);
            pipelineSummary = await _runPipelineAsync(collector, cancellationToken);

            var status = IsDegraded(preflightResult, pipelineSummary, collector)
                ? ScheduledRunStatus.Degraded
                : ScheduledRunStatus.Success;
            var completed = CreateResult(
                status,
                preflightResult,
                ingestSummary,
                pipelineSummary,
                collector);
            Report(completed);
            return completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cancelled = CreateResult(
                ScheduledRunStatus.Cancelled,
                preflightResult,
                ingestSummary,
                pipelineSummary,
                collector);
            Report(cancelled);
            return cancelled;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled JobLens run failed unexpectedly.");
            var fatal = CreateResult(
                ScheduledRunStatus.Fatal,
                preflightResult,
                ingestSummary,
                pipelineSummary,
                collector,
                ex.Message);
            Report(fatal);
            return fatal;
        }
    }

    private static bool IsDegraded(
        PreflightResult preflight,
        RunSummary pipeline,
        RunDetailCollector collector) =>
        preflight.Status == PreflightStatus.Degraded ||
        pipeline.StoppedEarly ||
        pipeline.TailoringFailures > 0 ||
        collector.ReziAuthenticationError is not null ||
        collector.ScoringTransportFailures.Count > 0;

    private static ScheduledRunResult CreateResult(
        ScheduledRunStatus status,
        PreflightResult? preflight,
        IngestSummary? ingest,
        RunSummary? pipeline,
        RunDetailCollector collector,
        string? fatalError = null) =>
        new(
            status,
            preflight,
            ingest,
            pipeline,
            collector.Matches,
            collector.ReziAuthenticationError,
            collector.ScoringTransportFailures,
            fatalError);

    private void Report(ScheduledRunResult result)
    {
        _logger.LogInformation(
            "Scheduled run summary: status={Status} exitCode={ExitCode} build={Build} " +
            "preflightStatus={PreflightStatus} bridgeHealthy={BridgeHealthy} " +
            "latestConfiguredGroupMessage={LatestMessage} preflightWarnings={WarningCount} " +
            "preflightFailures={FailureCount} fetched={Fetched} parsed={Parsed} " +
            "filteredOut={FilteredOut} alreadyStored={AlreadyStored} newlyStored={NewlyStored} " +
            "batches={Batches} scored={Scored} matched={Matched} notified={Notified} " +
            "draftsCreated={DraftsCreated} draftsReused={DraftsReused} " +
            "tailoringFailures={TailoringFailures} stoppedEarly={StoppedEarly} " +
            "stopReason={StopReason} notifiedMatches={MatchCount} reziAuthFailed={ReziAuthFailed} " +
            "scoringTransportFailures={ScoringTransportFailureCount}",
            result.Status,
            result.ExitCode,
            result.Preflight?.Build ?? "unavailable",
            result.Preflight?.Status.ToString() ?? "not-run",
            result.Preflight?.BridgeHealthy?.ToString() ?? "unavailable",
            result.Preflight?.LatestConfiguredGroupMessage?.ToString("O") ?? "unavailable",
            result.Preflight?.Warnings.Count ?? 0,
            result.Preflight?.Failures.Count ?? 0,
            result.Ingest?.Fetched ?? 0,
            result.Ingest?.Parsed ?? 0,
            result.Ingest?.FilteredOut ?? 0,
            result.Ingest?.AlreadyStored ?? 0,
            result.Ingest?.NewlyStored ?? 0,
            result.Pipeline?.Batches ?? 0,
            result.Pipeline?.Scored ?? 0,
            result.Pipeline?.Matched ?? 0,
            result.Pipeline?.Notified ?? 0,
            result.Pipeline?.DraftsCreated ?? 0,
            result.Pipeline?.DraftsReused ?? 0,
            result.Pipeline?.TailoringFailures ?? 0,
            result.Pipeline?.StoppedEarly ?? false,
            result.Pipeline?.StopReason ?? "none",
            result.Matches.Count,
            result.ReziAuthenticationError is not null,
            result.ScoringTransportFailures.Count);

        if (result.Status == ScheduledRunStatus.LockUnavailable)
        {
            _logger.LogWarning(
                "Scheduled JobLens run did not start because another pipeline run holds the shared lock.");
        }

        if (result.FatalError is not null)
            _logger.LogError("Scheduled run fatal error: {FatalError}", result.FatalError);

        if (result.Preflight is not null)
        {
            foreach (var warning in result.Preflight.Warnings)
                _logger.LogWarning("Scheduled run preflight warning: {Warning}", warning);
            foreach (var failure in result.Preflight.Failures)
                _logger.LogError("Scheduled run preflight failure: {Failure}", failure);
        }

        foreach (var match in result.Matches)
        {
            _logger.LogInformation(
                "Scheduled match: messageId={MessageId} title={Title} company={Company} score={Score} " +
                "selectedTemplate={SelectedTemplate} draftOutcome={DraftOutcome} draftId={DraftId}",
                match.MessageId,
                match.Title,
                match.Company,
                match.Score,
                match.SelectedTemplate ?? "none",
                match.DraftOutcome,
                match.DraftId ?? "none");
        }

        if (result.ReziAuthenticationError is not null)
        {
            _logger.LogWarning(
                "Scheduled run Rezi authentication error: {ReziAuthenticationError}",
                result.ReziAuthenticationError);
        }

        foreach (var failure in result.ScoringTransportFailures)
        {
            _logger.LogWarning(
                "Scheduled run scoring transport failure: template={TemplateName} " +
                "exceptionType={ExceptionType} message={Message}",
                failure.TemplateName,
                failure.ExceptionType,
                failure.Message);
        }
    }
}
