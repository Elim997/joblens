namespace JobLens.Core.Resume;

public record BaseResumeSummary(string Id, string Name, string Summary);

public record BaseSelection(string BaseResumeId, string BaseResumeName, string Rationale);

/// <summary>ItemId is an existing item key from the chosen base resume - never a new one.</summary>
public record TailoredExperienceItem(string ItemId, string Description);

/// <summary>ItemId is an existing item key from the chosen base resume - never a new one.</summary>
public record TailoredSkillItem(string ItemId, string Skill);

public record TailoredResume(
    BaseSelection BaseSelection,
    string Summary,
    IReadOnlyList<TailoredExperienceItem> Experience,
    IReadOnlyList<TailoredSkillItem> Skills,
    string RewriteRationale);
