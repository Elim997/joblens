namespace JobLens.Core.Parsing;

/// <summary>
/// Drops postings whose Category is not in the target set. Runs after parsing
/// and before anything expensive (embedding, LLM scoring).
/// </summary>
public static class CategoryFilter
{
    public static IEnumerable<JobPosting> FilterToTargetCategories(
        IEnumerable<JobPosting> postings, IReadOnlyCollection<string> targetCategories)
    {
        var targets = ToTargetSet(targetCategories);
        return postings.Where(p => targets.Contains(p.Category));
    }

    /// <summary>
    /// Builds the target-category set once, so callers that cannot filter a bare
    /// JobPosting sequence still match categories by the exact same rule. IngestService
    /// needs this: it must keep every posting paired with its source message id (for the
    /// already-stored dedupe and the upsert), which a JobPosting-in/JobPosting-out filter
    /// would discard. Case-insensitive because the feed's Category casing is the job bot's
    /// to decide, not ours.
    /// </summary>
    public static HashSet<string> ToTargetSet(IReadOnlyCollection<string> targetCategories) =>
        new(targetCategories, StringComparer.OrdinalIgnoreCase);
}
