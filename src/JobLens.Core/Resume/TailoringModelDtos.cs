namespace JobLens.Core.Resume;

/// <summary>
/// One experience/skill rewrite entry, exactly as the model returned it. Experience items carry
/// their text in Description, skill items in Skill - ResumeTailoringValidator reads the field
/// that matches the section it's validating rather than merging the two, so a model that fills
/// in the wrong field for a section is treated as blank text, not silently accepted from the
/// other field.
/// </summary>
internal sealed record UntrustedRewriteItem(string? ItemId, string? Description, string? Skill);

/// <summary>
/// Raw tailoring rewrite JSON, deserialized but not trusted: every field is nullable (including
/// list elements - an array entry can itself be JSON `null`) so a missing key, wrong type, or
/// truncated response shows up as null/absent here instead of a permissive default. The only way
/// to turn this into a ValidatedTailoredResume is ResumeTailoringValidator.Validate.
/// </summary>
internal sealed record UntrustedRewrite(
    string? Summary,
    List<UntrustedRewriteItem?>? Experience,
    List<UntrustedRewriteItem?>? Skills,
    string? Rationale);
