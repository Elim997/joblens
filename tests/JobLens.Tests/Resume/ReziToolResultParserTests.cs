using JobLens.Core.Resume;
using System.Text.Json;

namespace JobLens.Tests.Resume;

public class ReziToolResultParserTests
{
    [Fact]
    public void Parse_HybridTextThenJson_ParsesTheJsonBodyWithoutThrowing()
    {
        var text = "Resume updated (id: abc123)\n\n{\"id\": \"abc123\", \"name\": \"for edit\"}";

        var node = ReziToolResultParser.Parse("write_resume", isError: null, text);

        Assert.NotNull(node);
        Assert.Equal("abc123", node["id"]!.GetValue<string>());
        Assert.Equal("for edit", node["name"]!.GetValue<string>());
    }

    [Fact]
    public void Parse_PureJsonResponse_StillParsesCorrectly()
    {
        var text = """[{"id": "r1", "name": "QA Automation Developer"}]""";

        var node = ReziToolResultParser.Parse("list_resumes", isError: false, text);

        Assert.NotNull(node);
        Assert.Equal("r1", node[0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public void Parse_IsErrorTrue_ThrowsReziToolCallException_NotJsonReaderException()
    {
        var ex = Assert.Throws<ReziToolCallException>(() =>
            ReziToolResultParser.Parse("write_resume", isError: true, "Resume not found"));

        Assert.Equal("write_resume", ex.ToolName);
        Assert.Equal("Resume not found", ex.ReziError);
        Assert.Contains("write_resume", ex.Message);
        Assert.Contains("Resume not found", ex.Message);
    }

    [Fact]
    public void Parse_IsErrorTrueWithNoText_StillThrowsTypedException()
    {
        var ex = Assert.Throws<ReziToolCallException>(() =>
            ReziToolResultParser.Parse("write_resume", isError: true, text: null));

        Assert.Equal("(no error detail returned)", ex.ReziError);
    }

    [Fact]
    public void Parse_NullText_ReturnsNull()
    {
        Assert.Null(ReziToolResultParser.Parse("get_resume_format", isError: null, text: null));
    }

    [Fact]
    public void Parse_PlainTextWithNoJsonBody_ReturnsNullRatherThanThrowing()
    {
        Assert.Null(ReziToolResultParser.Parse("some_tool", isError: false, "Done."));
    }

    [Fact]
    public void Parse_MalformedJsonAfterPrefix_ThrowsJsonException()
    {
        // Distinguishing from the fix: once we've found the '{', it must still be valid JSON.
        // Not a scenario Rezi has produced - just proves this isn't silently swallowed either.
        Assert.ThrowsAny<JsonException>(() =>
            ReziToolResultParser.Parse("write_resume", isError: null, "Resume updated\n\n{not valid json"));
    }
}
