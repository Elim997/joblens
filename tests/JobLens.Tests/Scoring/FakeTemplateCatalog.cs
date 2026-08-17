using JobLens.Core.Scoring;

namespace JobLens.Tests.Scoring;

public class FakeTemplateCatalog(params ScoringTemplate[] templates) : ITemplateCatalog
{
    public int CallCount { get; private set; }

    public Task<IReadOnlyList<ScoringTemplate>> GetTemplatesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;
        return Task.FromResult<IReadOnlyList<ScoringTemplate>>(templates);
    }
}
