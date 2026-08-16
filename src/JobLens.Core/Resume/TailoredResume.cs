namespace JobLens.Core.Resume;

public record BaseResumeSummary(string Id, string Name, string Summary);

public record BaseSelection(string BaseResumeId, string BaseResumeName, string Rationale);

/// <summary>ItemId is an existing item key from the chosen base resume - never a new one.</summary>
public record TailoredExperienceItem(string ItemId, string Description);

/// <summary>ItemId is an existing item key from the chosen base resume - never a new one.</summary>
public record TailoredSkillItem(string ItemId, string Skill);

// The tailoring result itself is ValidatedTailoredResume (see ValidatedTailoredResume.cs) - its
// constructor is internal to ResumeTailoringValidator, unlike this file's freely constructible
// records, so no unvalidated model output can cross the tailoring boundary as a trusted result.
