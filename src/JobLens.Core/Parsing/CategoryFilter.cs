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
        var targets = new HashSet<string>(targetCategories, StringComparer.OrdinalIgnoreCase);
        return postings.Where(p => targets.Contains(p.Category));
    }
}
