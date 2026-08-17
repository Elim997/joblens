using JobLens.Core.Parsing;
using JobLens.Core.Resume;

namespace JobLens.Tests.Resume;

// Returns a canned ValidatedTailoredResume (or throws a configured exception) under test control,
// so TailoredDraftServiceTests/TailorEndpointTests can isolate the service's/endpoint's own
// persistence/guardrail/status-code logic from LlmResumeTailor's behavior (already covered by
// LlmResumeTailorTests). Honors cancellation the same way the real tailor's model calls do.
public class FakeResumeTailor : IResumeTailor
{
    private readonly ValidatedTailoredResume? _result;
    private readonly Exception? _exception;
    private readonly Func<JobPosting, ValidatedTailoredResume>? _resultFactory;

    public FakeResumeTailor(ValidatedTailoredResume result) => _result = result;

    public FakeResumeTailor(Exception exception) => _exception = exception;

    // Per-posting success/failure branching: lets a single test make tailoring succeed for some
    // postings and throw for others (e.g. one strong match's auto-tailor fails while another's
    // succeeds in the same /run) without touching the two constructors/call sites above.
    public FakeResumeTailor(Func<JobPosting, ValidatedTailoredResume> resultFactory) =>
        _resultFactory = resultFactory;

    public JobPosting? LastPosting { get; private set; }

    public string? LastBaseResumeId { get; private set; }

    public string? LastBaseResumeName { get; private set; }

    public int CallCount { get; private set; }

    public Task<ValidatedTailoredResume> TailorAsync(
        JobPosting posting,
        string baseResumeId,
        string baseResumeName,
        CancellationToken cancellationToken = default)
    {
        LastPosting = posting;
        LastBaseResumeId = baseResumeId;
        LastBaseResumeName = baseResumeName;
        CallCount++;
        cancellationToken.ThrowIfCancellationRequested();

        if (_exception is not null)
            throw _exception;

        if (_resultFactory is not null)
            return Task.FromResult(_resultFactory(posting));

        return Task.FromResult(_result!);
    }
}
