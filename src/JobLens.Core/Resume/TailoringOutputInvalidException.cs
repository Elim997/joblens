namespace JobLens.Core.Resume;

/// <summary>
/// The tailoring model responded, but after retries its output still could not be parsed into a
/// usable DTO (malformed JSON, unexpected shape). Distinct from
/// ResumeTailoringValidationException, which means the DTO parsed fine but failed validation
/// against the base resume it was derived from.
/// </summary>
public sealed class TailoringOutputInvalidException(string message) : InvalidOperationException(message);
