using JobLens.Core.Parsing;

namespace JobLens.Core.Resume;

public interface IResumeTailor
{
    /// <summary>
    /// Picks the best-fit base resume for <paramref name="posting"/>, then rewrites its
    /// summary/experience/skills to the posting. Read-only: never writes anything, and never
    /// touches a base resume other than to read it. Writing the result is Phase 3's job. The
    /// returned ValidatedTailoredResume has already passed ResumeTailoringValidator - no
    /// unvalidated model output ever crosses this boundary.
    /// </summary>
    Task<ValidatedTailoredResume> TailorAsync(JobPosting posting, CancellationToken cancellationToken = default);
}
