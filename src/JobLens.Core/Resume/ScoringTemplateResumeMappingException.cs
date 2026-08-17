namespace JobLens.Core.Resume;

/// <summary>
/// The posting's persisted selected_template no longer matches any configured Rezi:BaseResumes
/// entry by name - the 1:1 scoring-template-to-base-resume mapping has drifted out of config
/// since the posting was scored. A JobLens config bug, not an upstream failure, so this fails
/// closed rather than silently falling back to a different base: the historical selection is
/// never remapped automatically. Fixing the config (or rescoring under a corrected template set)
/// is the intended remedy.
/// </summary>
public sealed class ScoringTemplateResumeMappingException(string templateName)
    : InvalidOperationException($"Scoring template '{templateName}' has no configured matching Rezi base resume (Rezi:BaseResumes).");
