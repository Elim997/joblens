using JobLens.Core.Embedding;
using JobLens.Core.Parsing;

namespace JobLens.Tests.Embedding;

public class JobPostingTextNormalizerTests
{
    [Fact]
    public void ToEmbeddingText_UsesParsedFieldsNotRawMessage()
    {
        var posting = new JobPosting(
            Title: "Junior Algorithm & Software Developer",
            Company: "E2E Solutions IL",
            Location: "Haifa",
            Category: "Software",
            ApplyUrl: "https://www.linkedin.com/jobs/view/4450955364",
            Description: "- B.Sc. in Software Engineering, Computer Science, Electrical Engineering, Mathematics, or a related field");

        var text = JobPostingTextNormalizer.ToEmbeddingText(posting);

        Assert.Equal(
            "Junior Algorithm & Software Developer at E2E Solutions IL (Haifa, Software)\n" +
            "- B.Sc. in Software Engineering, Computer Science, Electrical Engineering, Mathematics, or a related field",
            text);
        Assert.DoesNotContain(posting.ApplyUrl, text);
    }
}
