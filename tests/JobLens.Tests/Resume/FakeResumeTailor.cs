using JobLens.Core.Parsing;
using JobLens.Core.Resume;

namespace JobLens.Tests.Resume;

// Returns a canned TailoredResume under test control, so ResumeTailoringRunnerTests can
// isolate the runner's own commit/guardrail/404 logic from GeminiResumeTailor's behavior
// (already covered by GeminiResumeTailorTests).
public class FakeResumeTailor(TailoredResume result) : IResumeTailor
{
    public JobPosting? LastPosting { get; private set; }

    public Task<TailoredResume> TailorAsync(JobPosting posting, CancellationToken cancellationToken = default)
    {
        LastPosting = posting;
        return Task.FromResult(result);
    }
}
