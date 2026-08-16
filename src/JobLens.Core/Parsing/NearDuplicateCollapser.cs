namespace JobLens.Core.Parsing;

/// <summary>
/// Collapses postings that are the same job posted more than once - the WhatsApp feed
/// sometimes reposts a listing verbatim, or with a slightly different company-name
/// spelling. Keys on ApplyUrl (the strongest signal a repost shares), falling back to
/// Title+Company when ApplyUrl is blank. This runs only at the notify/query surface -
/// storage keeps one row per message_id, unchanged, so nothing is deleted or merged.
/// </summary>
public static class NearDuplicateCollapser
{
    /// <summary>
    /// Keeps the first occurrence of each duplicate key and drops the rest, so callers
    /// that pass already-ranked input (e.g. scored-descending) keep the best-ranked copy.
    /// Pass an external <paramref name="seenKeys"/> (created with
    /// StringComparer.OrdinalIgnoreCase) to dedupe across multiple calls - e.g. across
    /// every batch of one multi-batch /run - instead of only within this call's items;
    /// omit it for the original single-call behavior.
    /// </summary>
    public static IReadOnlyList<T> Collapse<T>(
        IReadOnlyList<T> items, Func<T, JobPosting> postingSelector, HashSet<string>? seenKeys = null)
    {
        seenKeys ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<T>();
        foreach (var item in items)
        {
            var posting = postingSelector(item);
            var key = string.IsNullOrWhiteSpace(posting.ApplyUrl)
                ? $"title:{posting.Title}|company:{posting.Company}"
                : $"url:{posting.ApplyUrl}";
            if (seenKeys.Add(key))
                result.Add(item);
        }

        return result;
    }
}
