using JobLens.Core.Parsing;

namespace JobLens.Tests.Parsing;

public class CategoryFilterTests
{
    [Fact]
    public void FilterToTargetCategories_DropsOffTargetCategories()
    {
        var postings = new[]
        {
            new JobPosting("QA Engineer", "Acme", "Tel Aviv", "QA", "https://example.com/1", ""),
            new JobPosting("Backend Dev", "Acme", "Haifa", "Software", "https://example.com/2", ""),
            new JobPosting("Electrical Engineer", "Acme", "Herzliya", "Hardware", "https://example.com/3", ""),
            new JobPosting("Research Scientist", "Acme", "Rehovot", "Research", "https://example.com/4", ""),
        };

        var result = CategoryFilter.FilterToTargetCategories(postings, ["Software", "QA"]).ToList();

        Assert.Equal(2, result.Count);
        Assert.Contains(result, p => p.Category == "QA");
        Assert.Contains(result, p => p.Category == "Software");
    }

    [Fact]
    public void FilterToTargetCategories_IsCaseInsensitive()
    {
        var postings = new[]
        {
            new JobPosting("QA Engineer", "Acme", "Tel Aviv", "qa", "https://example.com/1", ""),
        };

        var result = CategoryFilter.FilterToTargetCategories(postings, ["QA"]).ToList();

        Assert.Single(result);
    }
}
