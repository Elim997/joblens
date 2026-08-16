namespace JobLens.Core.Resume;

/// <summary>
/// The configured Rezi write destination (Rezi:ForEditResumeId) is unsafe to write to: missing,
/// blank, or equal to one of the configured base resume ids. Thrown by ResumeTailoringRunner
/// before any tailoring model call or Rezi read for a commit=true request - a config-integrity
/// guard, not a tailoring-logic one, since the write destination comes only from configuration
/// and the model never influences it. Never includes the configured resume id in its message:
/// base/for-edit resume ids identify this account's resumes the same way GroupChatJids does.
/// </summary>
public sealed class ResumeWriteConfigurationException(string message) : InvalidOperationException(message);
