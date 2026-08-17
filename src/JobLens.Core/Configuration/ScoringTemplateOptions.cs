namespace JobLens.Core.Configuration;

// One entry per scoring template. Name-aligns with the matching Rezi base resume in
// ReziOptions.BaseResumes (1:1 mapping for now, see CLAUDE.md/JobLensOptions), but that
// mapping is by convention only - nothing here reads ReziOptions, and scoring never
// contacts Rezi.
public class ScoringTemplateOptions
{
    public string Name { get; set; } = "";
    public string Profile { get; set; } = "";
}
