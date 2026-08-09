using JobLens.Core.Parsing;

namespace JobLens.Core.Embedding;

public static class JobPostingTextNormalizer
{
    // Embeds the parsed fields, not the raw WhatsApp message: markup, LRM marks, and
    // shared boilerplate would otherwise dominate similarity between unrelated postings.
    public static string ToEmbeddingText(JobPosting posting) =>
        $"{posting.Title} at {posting.Company} ({posting.Location}, {posting.Category})\n{posting.Description}";
}
