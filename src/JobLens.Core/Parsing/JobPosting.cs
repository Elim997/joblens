namespace JobLens.Core.Parsing;

public record JobPosting(
    string Title,
    string Company,
    string Location,
    string Category,
    string ApplyUrl,
    string Description);
