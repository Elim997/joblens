namespace JobLens.Core.Scoring;

// A configured scoring template with its profile text pre-embedded, so LlmRelevanceScorer
// never re-embeds a template profile per call.
public record ScoringTemplate(string Name, string Profile, float[] Embedding);

public interface ITemplateCatalog
{
    /// <summary>
    /// The configured scoring templates (JobLens:ScoringTemplates), each embedded once and
    /// cached for the lifetime of the catalog - never read from Rezi, never re-embedded per
    /// call. Startup validation (Program.ValidateRequiredConfig) guarantees this is
    /// non-empty in a running app; a hermetic caller (e.g. a test fake) may still return an
    /// empty list, which callers must handle as "nothing to route against."
    /// </summary>
    Task<IReadOnlyList<ScoringTemplate>> GetTemplatesAsync(CancellationToken cancellationToken = default);
}
