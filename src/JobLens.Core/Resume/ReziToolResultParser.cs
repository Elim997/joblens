using System.Text.Json.Nodes;

namespace JobLens.Core.Resume;

/// <summary>
/// Turns a Rezi MCP tool call's raw result into JsonNode, or throws. Pulled out of
/// RealResumeClient as a pure function so it's directly unit-testable without a live
/// connection - see ReziToolResultParserTests.
/// </summary>
public static class ReziToolResultParser
{
    public static JsonNode? Parse(string toolName, bool? isError, string? text)
    {
        if (isError == true)
            throw new ReziToolCallException(toolName, string.IsNullOrWhiteSpace(text) ? "(no error detail returned)" : text);

        if (string.IsNullOrEmpty(text))
            return null;

        // Most tools (list_resumes, read_resume, get_resume_format) return pure JSON text.
        // write_resume instead prefixes a human-readable confirmation line before the JSON
        // body - "Resume updated (id: ...)\n\n{...}" - so don't assume position 0 is JSON;
        // find where it actually starts. No '{' or '[' at all means a plain text confirmation
        // with no structured body to return.
        var jsonStart = text.IndexOfAny(['{', '[']);
        return jsonStart < 0 ? null : JsonNode.Parse(text[jsonStart..]);
    }
}
