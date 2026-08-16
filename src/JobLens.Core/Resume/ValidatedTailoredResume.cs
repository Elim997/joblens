namespace JobLens.Core.Resume;

/// <summary>
/// A tailoring rewrite that has passed ResumeTailoringValidator. This is the only way to obtain
/// one: the constructor is internal, so nothing outside JobLens.Core (or JobLens.Tests, via
/// InternalsVisibleTo) can hand-build a result and skip validation. Carries the selected base
/// resume's original item ids alongside the merged result, so a later step (the Rezi write) can
/// re-check completeness against the base without re-deriving it from a second live read.
/// </summary>
public sealed class ValidatedTailoredResume
{
    public BaseSelection BaseSelection { get; }
    public string Summary { get; }
    public IReadOnlyList<TailoredExperienceItem> Experience { get; }
    public IReadOnlyList<TailoredSkillItem> Skills { get; }
    public string RewriteRationale { get; }

    /// <summary>Every experience item id present on the selected base resume - the exact set
    /// Experience covers, each exactly once.</summary>
    public IReadOnlyList<string> OriginalExperienceIds { get; }

    /// <summary>Every skill item id present on the selected base resume - the exact set Skills
    /// covers, each exactly once.</summary>
    public IReadOnlyList<string> OriginalSkillIds { get; }

    internal ValidatedTailoredResume(
        BaseSelection baseSelection,
        string summary,
        IReadOnlyList<TailoredExperienceItem> experience,
        IReadOnlyList<TailoredSkillItem> skills,
        string rewriteRationale,
        IReadOnlyList<string> originalExperienceIds,
        IReadOnlyList<string> originalSkillIds)
    {
        BaseSelection = baseSelection;
        Summary = summary;
        Experience = Array.AsReadOnly(experience.ToArray());
        Skills = Array.AsReadOnly(skills.ToArray());
        RewriteRationale = rewriteRationale;
        OriginalExperienceIds = Array.AsReadOnly(originalExperienceIds.ToArray());
        OriginalSkillIds = Array.AsReadOnly(originalSkillIds.ToArray());
    }
}
