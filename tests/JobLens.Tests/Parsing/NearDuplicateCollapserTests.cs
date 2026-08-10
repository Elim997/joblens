using JobLens.Core.Parsing;

namespace JobLens.Tests.Parsing;

public class NearDuplicateCollapserTests
{
    private static JobPosting MakePosting(string title, string company, string applyUrl) =>
        new(title, company, "Tel Aviv", "Software", applyUrl, "- test requirement");

    [Fact]
    public void Collapse_TwoPostingsShareApplyUrl_KeepsOnlyTheFirst()
    {
        var first = MakePosting("Backend Engineer", "Acme", "https://example.com/job/123");
        var second = MakePosting("Backend Engineer", "Acme Inc.", "https://example.com/job/123");

        var result = NearDuplicateCollapser.Collapse([first, second], p => p);

        Assert.Equal([first], result);
    }

    [Fact]
    public void Collapse_ApplyUrlComparisonIsCaseInsensitive()
    {
        var first = MakePosting("Backend Engineer", "Acme", "https://Example.com/job/123");
        var second = MakePosting("Backend Engineer", "Acme", "https://example.com/JOB/123");

        // Different casing is still the same URL string once lowercased; this collapser
        // only guards against literal case differences, not URL normalization.
        var result = NearDuplicateCollapser.Collapse([first, second], p => p);

        Assert.Equal([first], result);
    }

    [Fact]
    public void Collapse_BlankApplyUrl_FallsBackToTitleAndCompany()
    {
        var first = MakePosting("QA Engineer", "Beta", applyUrl: "");
        var second = MakePosting("QA Engineer", "Beta", applyUrl: "");

        var result = NearDuplicateCollapser.Collapse([first, second], p => p);

        Assert.Equal([first], result);
    }

    [Fact]
    public void Collapse_DifferentApplyUrls_KeepsBoth()
    {
        var first = MakePosting("Backend Engineer", "Acme", "https://example.com/job/123");
        var second = MakePosting("Backend Engineer", "Acme", "https://example.com/job/456");

        var result = NearDuplicateCollapser.Collapse([first, second], p => p);

        Assert.Equal([first, second], result);
    }

    [Fact]
    public void Collapse_BlankApplyUrlWithDifferentCompany_KeepsBoth()
    {
        var first = MakePosting("QA Engineer", "Beta", applyUrl: "");
        var second = MakePosting("QA Engineer", "Gamma", applyUrl: "");

        var result = NearDuplicateCollapser.Collapse([first, second], p => p);

        Assert.Equal([first, second], result);
    }

    [Fact]
    public void Collapse_EmptyInput_ReturnsEmpty()
    {
        var result = NearDuplicateCollapser.Collapse(Array.Empty<JobPosting>(), p => p);

        Assert.Empty(result);
    }
}
