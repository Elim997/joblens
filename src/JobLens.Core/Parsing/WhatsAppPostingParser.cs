using System.Text.RegularExpressions;
using JobLens.Core.Feed;

namespace JobLens.Core.Parsing;

/// <summary>
/// Deterministic parser for the job-bot's fixed message layout:
/// bold "*Title* [ref] / Company" line, italic "_Location_ | _Category_" line,
/// requirement bullets, an apply link, and optional referally.* boilerplate.
/// Messages that don't match this layout (promos, ads) are not job posts and
/// parse to null rather than being force-fit.
/// </summary>
public partial class WhatsAppPostingParser : IPostingParser
{
    private static readonly string[] BoilerplateMarkers =
    [
        "join our community",
        "referally.link",
        "referally-jobos.lovable.app",
        "referally.setmore.com",
    ];

    public JobPosting? Parse(RawMessage message)
    {
        // WhatsApp inserts LEFT-TO-RIGHT MARK (U+200E) throughout bidi text; it
        // carries no content and would otherwise break line/section matching.
        var content = message.Content.Replace("‎", string.Empty);
        var lines = content
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        if (lines.Count < 2)
            return null;

        var titleMatch = TitleLineRegex().Match(lines[0]);
        if (!titleMatch.Success)
            return null;

        var title = titleMatch.Groups["title"].Value.Trim();
        var slashIndex = titleMatch.Groups["rest"].Value.IndexOf('/');
        if (slashIndex < 0)
            return null;

        var company = titleMatch.Groups["rest"].Value[(slashIndex + 1)..].Trim();
        if (company.Length == 0)
            return null;

        var locationCategoryMatch = LocationCategoryLineRegex().Match(lines[1]);
        if (!locationCategoryMatch.Success)
            return null;

        var location = locationCategoryMatch.Groups["location"].Value.Trim();
        var category = locationCategoryMatch.Groups["category"].Value.Trim();

        var bodyLines = lines.Skip(2).Where(l => !IsBoilerplate(l)).ToList();
        var applyUrl = ExtractApplyUrl(bodyLines);
        var description = string.Join('\n', bodyLines.Where(l => !UrlRegex().IsMatch(l)));

        return new JobPosting(title, company, location, category, applyUrl, description);
    }

    private static string ExtractApplyUrl(IEnumerable<string> bodyLines)
    {
        foreach (var line in bodyLines)
        {
            foreach (Match match in UrlRegex().Matches(line))
            {
                if (!match.Value.Contains("referally", StringComparison.OrdinalIgnoreCase))
                    return match.Value;
            }
        }

        return string.Empty;
    }

    private static bool IsBoilerplate(string line) =>
        BoilerplateMarkers.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase));

    // *Title* [optional ref] / Company - captures the bolded title and everything after it.
    [GeneratedRegex(@"^\*(?<title>.+?)\*(?<rest>.*)$")]
    private static partial Regex TitleLineRegex();

    // _Location_ | _Category_
    [GeneratedRegex(@"^_(?<location>.+?)_\s*\|\s*_(?<category>.+?)_$")]
    private static partial Regex LocationCategoryLineRegex();

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex UrlRegex();
}
