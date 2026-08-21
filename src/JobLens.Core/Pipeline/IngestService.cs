using JobLens.Core.Configuration;
using JobLens.Core.Embedding;
using JobLens.Core.Feed;
using JobLens.Core.Parsing;
using JobLens.Core.Storage;
using Microsoft.Extensions.Options;

namespace JobLens.Core.Pipeline;

/// <param name="Fetched">Raw messages the feed source returned this call.</param>
/// <param name="Parsed">Of those, how many matched the job-bot's fixed layout. The rest
/// (promos, boilerplate, media-only) are skipped by the parser, not counted as filtered out.</param>
/// <param name="FilteredOut">Parsed postings whose Category is not in TargetCategories.</param>
/// <param name="AlreadyStored">On-target postings whose message id is already in pgvector -
/// never re-embedded, so a repeated ingest costs nothing in embedding quota.</param>
/// <param name="NewlyStored">On-target postings embedded and upserted this call. These are
/// left with scored_at NULL on purpose: scoring happens only on the backlog-drain path
/// (POST /run), never here.</param>
public record IngestSummary(
    int Fetched,
    int Parsed,
    int FilteredOut,
    int AlreadyStored,
    int NewlyStored);

/// <summary>
/// The ingest half of the pipeline: fetch -> parse -> category filter -> dedupe against
/// what's already stored -> embed -> upsert. Nothing more.
///
/// It deliberately has no IRelevanceScorer dependency. Scoring, notification and drafting
/// belong exclusively to the backlog-drain path (PipelineRunner, behind POST /run and the
/// scheduled run), which is the only code that calls MarkScoredAsync. Keeping the two apart
/// is what makes every posting get scored exactly once, no matter which caller ingested it:
/// a posting scored here would either be paid for twice (scored again by the next /run) or,
/// if marked scored to avoid that, drop out of GetUnscoredPostingsAsync's scored_at IS NULL
/// filter and become permanently invisible - never notified, never drafted.
///
/// Both POST /ingest and the scheduled runner call this one implementation, so there is no
/// second, drifting ingest path.
/// </summary>
public class IngestService(
    IJobFeedSource feedSource,
    IPostingParser parser,
    IEmbedder embedder,
    IDatastore datastore,
    IOptions<JobLensOptions> options)
{
    public async Task<IngestSummary> IngestAsync(CancellationToken cancellationToken = default)
    {
        var messages = await feedSource.GetMessagesAsync(cancellationToken);
        var parsed = messages.Select(m => (Message: m, Posting: parser.Parse(m))).ToList();
        var parsedCount = parsed.Count(x => x.Posting is not null);

        // Filtered by the shared CategoryFilter rule, but pair by pair: each posting has to
        // stay attached to its message id for the dedupe and upsert below.
        var targetCategories = CategoryFilter.ToTargetSet(options.Value.TargetCategories);
        var candidates = parsed
            .Where(x => x.Posting is not null && targetCategories.Contains(x.Posting.Category))
            .ToList();

        var existingIds = await datastore.GetExistingMessageIdsAsync(cancellationToken);
        var toEmbed = candidates.Where(x => !existingIds.Contains(x.Message.Id)).ToList();

        if (toEmbed.Count > 0)
        {
            // One batch call, not one call per posting - embedding quota is the scarce resource.
            var texts = toEmbed.Select(x => JobPostingTextNormalizer.ToEmbeddingText(x.Posting!)).ToList();
            var embeddings = await embedder.EmbedBatchAsync(texts, cancellationToken);

            await datastore.EnsureSchemaAsync(embeddings[0].Length, cancellationToken);
            for (var i = 0; i < toEmbed.Count; i++)
                await datastore.UpsertAsync(toEmbed[i].Message.Id, toEmbed[i].Posting!, embeddings[i], cancellationToken);
        }

        return new IngestSummary(
            Fetched: messages.Count,
            Parsed: parsedCount,
            FilteredOut: parsedCount - candidates.Count,
            AlreadyStored: candidates.Count - toEmbed.Count,
            NewlyStored: toEmbed.Count);
    }
}
