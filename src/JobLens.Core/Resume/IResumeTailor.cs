using JobLens.Core.Parsing;

namespace JobLens.Core.Resume;

public interface IResumeTailor
{
    /// <summary>
    /// Reads exactly the given base resume and rewrites its summary/experience/skills to fit
    /// posting. Which base to use is decided by the caller (TailoredDraftService, from the
    /// posting's persisted scoring template) - this method never chooses a base itself and never
    /// reads any resume other than baseResumeId. Read-only: never writes anything. The returned
    /// ValidatedTailoredResume has already passed ResumeTailoringValidator - no unvalidated model
    /// output ever crosses this boundary.
    /// </summary>
    Task<ValidatedTailoredResume> TailorAsync(
        JobPosting posting, string baseResumeId, string baseResumeName, CancellationToken cancellationToken = default);
}
