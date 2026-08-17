namespace JobLens.Core.Resume;

/// <summary>
/// The posting exists but has no persisted selected_template - either it has never been scored,
/// or it was scored before multi-template routing was introduced (a legacy row). Thrown by
/// TailoredDraftService before any tailoring model call or Rezi read; a rescore (POST /run) is
/// what makes the posting eligible, never a silent choice of base resume.
/// </summary>
public sealed class PostingNotScoredException(string messageId)
    : InvalidOperationException($"Posting '{messageId}' has not been scored with a selected template. Rescore it (POST /run) before tailoring.");
