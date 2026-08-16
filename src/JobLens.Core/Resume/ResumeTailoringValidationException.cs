namespace JobLens.Core.Resume;

/// <summary>
/// Tailoring model output parsed into a DTO but failed ResumeTailoringValidator's deterministic
/// checks against the base resume it was derived from - an invented item id, a duplicate id, a
/// missing/blank field, etc. Never thrown for a network/transport problem; see
/// TailoringModelUnavailableException for that, and TailoringOutputInvalidException for output
/// that never parsed into a DTO at all.
/// </summary>
public sealed class ResumeTailoringValidationException(string message) : InvalidOperationException(message);
