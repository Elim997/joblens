using JobLens.Core.Feed;
using JobLens.Core.Parsing;

namespace JobLens.Tests.Parsing;

public class WhatsAppPostingParserTests
{
    private const string ChatJid = "120363427094606388@g.us";

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

    private static RawMessage Raw(string id, string content) =>
        new(id, ChatJid, "120363427094606388", content, DateTimeOffset.Parse("2026-08-09T12:00:00+03:00"));

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
    public void Parse_Promo_NoTitleOrCategoryStructure_ReturnsNull()
    {
        var parser = new WhatsAppPostingParser();

        var posting = parser.Parse(Raw("promo-1", PromoContent));

        Assert.Null(posting);
    }
}
