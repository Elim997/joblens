using JobLens.Core.Feed;

namespace JobLens.Core.Parsing;

public interface IPostingParser
{
    /// <summary>
    /// Parses a raw message into a JobPosting, or null if the message does not
    /// match the job-bot's fixed layout (promo, boilerplate, or otherwise not a job post).
    /// </summary>
    JobPosting? Parse(RawMessage message);
}
