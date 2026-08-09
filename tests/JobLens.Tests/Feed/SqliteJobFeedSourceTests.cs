using JobLens.Core.Configuration;
using JobLens.Core.Feed;
using JobLens.Tests.TestSupport;
using Microsoft.Extensions.Options;

namespace JobLens.Tests.Feed;

public class SqliteJobFeedSourceTests
{
    private const string TargetChatJid = "120363427094606388@g.us";
    private const string SecondTargetChatJid = "111111111111111111@g.us";
    private const string OtherChatJid = "999999999@g.us";

    // Real captured content from the group (LEFT-TO-RIGHT MARK U+200E kept as-is).
    private const string RealJobContent1 =
        "‎*Field Operations -Rollout team member* / Exodigo\n‎\n‎_Tel Aviv_ | _QA_\n‎\n" +
        "- ‎Proven experience in field operations\n‎\n" +
        "‎https://www.comeet.com/jobs/exodigo/89.005/field-operations--rollout-team-member/36.F6B\n‎\n" +
        "‎Join our community: https://referally.link";

    private const string RealJobContent2 =
        "‎*Junior Algorithm & Software Developer* / E2E Solutions IL\n‎\n‎_Haifa_ | _Software_\n‎\n" +
        "- ‎B.Sc. in Software Engineering, Computer Science, Electrical Engineering, Mathematics, or a related field\n‎\n" +
        "‎https://www.linkedin.com/jobs/view/4450955364";

    [Fact]
    public async Task GetMessagesAsync_FiltersToTargetChatsSkipsMediaAndEmptyContent_OrderedByTimestamp()
    {
        using var db = new SqliteFixtureDb();

        // 2 real job rows, in the first target chat, no media.
        db.InsertMessage("job-1", TargetChatJid, "91384757371125", RealJobContent1, "2026-08-09 17:17:20+03:00", "");
        db.InsertMessage("job-2", TargetChatJid, "120363427094606388", RealJobContent2, "2026-08-09 16:12:47+03:00", "");

        // 1 real job row in a second target chat - multi-group support.
        db.InsertMessage("job-3", SecondTargetChatJid, "1", RealJobContent2, "2026-08-09 15:00:00+03:00", "");

        // 1 image-only row: empty content, media_type set. Skipped.
        db.InsertMessage("image-1", TargetChatJid, "120363427094606388", "", "2026-08-09 18:00:00+03:00", "image");

        // 1 row in a chat_jid that isn't in the target list. Skipped.
        db.InsertMessage("other-chat-1", OtherChatJid, "1", RealJobContent1, "2026-08-09 19:00:00+03:00", "");

        // 1 empty-content row in a target chat. Skipped.
        db.InsertMessage("empty-1", TargetChatJid, "120363427094606388", "", "2026-08-09 20:00:00+03:00", "");

        var options = Options.Create(new JobLensOptions
        {
            MessagesDbPath = db.Path,
            GroupChatJids = [TargetChatJid, SecondTargetChatJid],
        });
        var source = new SqliteJobFeedSource(options);

        var result = await source.GetMessagesAsync();

        Assert.Equal(3, result.Count);
        Assert.Equal("job-3", result[0].Id); // 15:00:00 earliest
        Assert.Equal("job-2", result[1].Id); // 16:12:47
        Assert.Equal("job-1", result[2].Id); // 17:17:20
        Assert.Equal(SecondTargetChatJid, result[0].ChatJid);
        Assert.All(result.Skip(1), m => Assert.Equal(TargetChatJid, m.ChatJid));
    }
}
