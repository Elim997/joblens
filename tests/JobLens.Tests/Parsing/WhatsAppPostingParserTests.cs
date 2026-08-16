using JobLens.Core.Feed;
using JobLens.Core.Parsing;

namespace JobLens.Tests.Parsing;

public class WhatsAppPostingParserTests
{
    private const string ChatJid = "test-group@g.us";

    // Real captured content from the group (LEFT-TO-RIGHT MARK U+200E kept as-is),
    // including the "Join our community" referally.link boilerplate block that a
    // real job post sometimes carries appended to it.
    private const string RealJobContent1 =
        "‎*Field Operations -Rollout team member* / Exodigo\n‎\n‎_Tel Aviv_ | _QA_\n‎\n" +
        "- ‎Proven experience in field operations\n‎\n" +
        "‎https://www.comeet.com/jobs/exodigo/89.005/field-operations--rollout-team-member/36.F6B\n‎\n" +
        "‎Join our community: https://referally.link";

    private const string RealJobContent2 =
        "‎*Junior Algorithm & Software Developer* / E2E Solutions IL\n‎\n‎_Haifa_ | _Software_\n‎\n" +
        "- ‎B.Sc. in Software Engineering, Computer Science, Electrical Engineering, Mathematics, or a related field\n‎\n" +
        "‎https://www.linkedin.com/jobs/view/4450955364";

    // A paid interview-prep promo: no bolded title, no Location | Category line.
    private const string PromoContent =
        "‎מעוניינים לשפר את הסיכויים שלכם בראיון העבודה הבא?\n‎\n" +
        "‎הצטרפו לשירות ההכנה לראיונות שלנו\n‎\n" +
        "‎https://referally.setmore.com/booking";

    // A real job post with a trailing ad block: a second *bold headline* promo
    // pitch appended after the requirements/apply URL, not the referally.link form.
    private const string RealJobContentWithAdBlock =
        "‎*Backend Developer* / TechCo\n‎\n‎_Tel Aviv_ | _Software_\n‎\n" +
        "- ‎3+ years experience with C# and .NET\n‎\n" +
        "‎https://example.com/careers/backend\n‎\n" +
        "‎*Book a prep session with Nicole* - ex-Google recruiter, now a hiring " +
        "interviewer helping candidates land offers >>";

    // Synthetic equivalents of recurring structures observed in the local archive;
    // real message text and identifiers stay out of the repository.
    private const string RealJobContentWithBoldHeaderLabel =
        "*QA Automation Engineer* / ExampleCo\n" +
        "*New opening*\n" +
        "_Tel Aviv_ | _QA_\n" +
        "- Experience with automated testing\n" +
        "https://example.com/jobs/qa-automation";

    private const string RealJobContentWithPlainHeaderMetadata =
        "*Junior Backend Developer* / ExampleCo\n" +
        "New opening\n" +
        "Reference 1234\n" +
        "_Haifa_ | _Software_\n" +
        "- Experience with C# and .NET\n" +
        "https://example.com/jobs/backend";

    private static RawMessage Raw(string id, string content) =>
        new(id, ChatJid, "test-sender", content, DateTimeOffset.Parse("2026-08-09T12:00:00+03:00"));

    [Fact]
    public void Parse_RealJobPost_ExtractsFieldsAndStripsBoilerplateWithoutDroppingMessage()
    {
        var parser = new WhatsAppPostingParser();

        var posting = parser.Parse(Raw("job-1", RealJobContent1));

        Assert.NotNull(posting);
        Assert.Equal("Field Operations -Rollout team member", posting!.Title);
        Assert.Equal("Exodigo", posting.Company);
        Assert.Equal("Tel Aviv", posting.Location);
        Assert.Equal("QA", posting.Category);
        Assert.Equal(
            "https://www.comeet.com/jobs/exodigo/89.005/field-operations--rollout-team-member/36.F6B",
            posting.ApplyUrl);
        Assert.Equal("- Proven experience in field operations", posting.Description);
        Assert.DoesNotContain("referally", posting.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RealJobPost_NoBoilerplateBlock_ExtractsFields()
    {
        var parser = new WhatsAppPostingParser();

        var posting = parser.Parse(Raw("job-2", RealJobContent2));

        Assert.NotNull(posting);
        Assert.Equal("Junior Algorithm & Software Developer", posting!.Title);
        Assert.Equal("E2E Solutions IL", posting.Company);
        Assert.Equal("Haifa", posting.Location);
        Assert.Equal("Software", posting.Category);
        Assert.Equal("https://www.linkedin.com/jobs/view/4450955364", posting.ApplyUrl);
    }

    [Fact]
    public void Parse_RealJobPost_TrailingBoldHeadlineAdBlock_ExcludedFromDescription()
    {
        var parser = new WhatsAppPostingParser();

        var posting = parser.Parse(Raw("job-3", RealJobContentWithAdBlock));

        Assert.NotNull(posting);
        Assert.Equal("Backend Developer", posting!.Title);
        Assert.Equal("TechCo", posting.Company);
        Assert.Equal("https://example.com/careers/backend", posting.ApplyUrl);
        Assert.Equal("- 3+ years experience with C# and .NET", posting.Description);
        Assert.DoesNotContain("Nicole", posting.Description);
        Assert.DoesNotContain("prep session", posting.Description);
        Assert.DoesNotContain("hiring interviewer", posting.Description);
    }

    [Theory]
    [InlineData("job-bold-header", RealJobContentWithBoldHeaderLabel, "QA Automation Engineer", "QA")]
    [InlineData("job-plain-header", RealJobContentWithPlainHeaderMetadata, "Junior Backend Developer", "Software")]
    public void Parse_RealJobPost_HeaderMetadataBeforeLocationCategory_ExtractsFields(
        string id,
        string content,
        string expectedTitle,
        string expectedCategory)
    {
        var parser = new WhatsAppPostingParser();

        var posting = parser.Parse(Raw(id, content));

        Assert.NotNull(posting);
        Assert.Equal(expectedTitle, posting!.Title);
        Assert.Equal(expectedCategory, posting.Category);
        Assert.DoesNotContain("New opening", posting.Description);
        Assert.DoesNotContain("Reference 1234", posting.Description);
    }

    [Fact]
    public void Parse_LocationCategoryAfterTrustedHeaderRegion_ReturnsNull()
    {
        var content =
            "*Backend Developer* / ExampleCo\n" +
            "Header one\n" +
            "Header two\n" +
            "Header three\n" +
            "_Tel Aviv_ | _Software_\n" +
            "- Experience with C# and .NET\n" +
            "https://example.com/jobs/backend";
        var parser = new WhatsAppPostingParser();

        var posting = parser.Parse(Raw("job-late-location", content));

        Assert.Null(posting);
    }

    [Fact]
    public void Parse_Promo_NoTitleOrCategoryStructure_ReturnsNull()
    {
        var parser = new WhatsAppPostingParser();

        var posting = parser.Parse(Raw("promo-1", PromoContent));

        Assert.Null(posting);
    }
}
