using System.Text.Json.Nodes;
using JobLens.Core.Configuration;
using JobLens.Core.Storage;
using Microsoft.Extensions.Options;

namespace JobLens.Core.Resume;

public record TailorResponse(
    string BaseResumeId,
    string BaseResumeName,
    string BaseSelectionRationale,
    string Summary,
    IReadOnlyList<TailoredExperienceItem> Experience,
    IReadOnlyList<TailoredSkillItem> Skills,
    string RewriteRationale,
    bool Committed,
    string? WrittenToResumeId)
{
    public static TailorResponse From(TailoredResume tailored, bool committed, string? writtenToResumeId) => new(
        tailored.BaseSelection.BaseResumeId,
        tailored.BaseSelection.BaseResumeName,
        tailored.BaseSelection.Rationale,
        tailored.Summary,
        tailored.Experience,
        tailored.Skills,
        tailored.RewriteRationale,
        committed,
        writtenToResumeId);
}

/// <summary>
/// Backs POST /tailor: loads the stored posting, runs IResumeTailor, and - only when
/// commit=true - writes the result into the configured "for edit" slot. The only place in
/// JobLens that ever calls IResumeClient.WriteResumeAsync.
/// </summary>
public class ResumeTailoringRunner(
    IDatastore datastore,
    IResumeTailor tailor,
    IResumeClient resumeClient,
    IOptions<ReziOptions> options)
{
    /// <summary>Null means no stored posting for messageId - the caller turns that into a 404.</summary>
    public async Task<TailorResponse?> RunAsync(string messageId, bool commit, CancellationToken cancellationToken = default)
    {
        var posting = await datastore.GetPostingByMessageIdAsync(messageId, cancellationToken);
        if (posting is null)
            return null;

        var tailored = await tailor.TailorAsync(posting, cancellationToken);

        if (!commit)
            return TailorResponse.From(tailored, committed: false, writtenToResumeId: null);

        var forEditId = options.Value.ForEditResumeId;

        // HARD GUARDRAIL: the only ever write target is the configured "for edit" slot - never
        // a base. This is a config-integrity check, not a tailoring-logic one: TailorAsync never
        // produces a write target at all (it only reads bases), so the only way a base could get
        // written is ForEditResumeId being misconfigured to equal one of the base ids.
        if (string.IsNullOrWhiteSpace(forEditId))
            throw new InvalidOperationException("Rezi:ForEditResumeId is not configured; refusing to write.");
        if (options.Value.BaseResumes.Any(b => b.Id == forEditId))
            throw new InvalidOperationException(
                $"Rezi:ForEditResumeId ('{forEditId}') matches a configured base resume id. " +
                "Refusing to write - a base must never be writable via /tailor.");

        await resumeClient.WriteResumeAsync(forEditId, BuildWritePayload(tailored), cancellationToken);
        return TailorResponse.From(tailored, committed: true, writtenToResumeId: forEditId);
    }

    private static JsonNode BuildWritePayload(TailoredResume tailored)
    {
        var experience = new JsonObject();
        foreach (var item in tailored.Experience)
            experience[item.ItemId] = new JsonObject { ["description"] = item.Description };

        var skills = new JsonObject();
        foreach (var item in tailored.Skills)
            skills[item.ItemId] = new JsonObject { ["skill"] = item.Skill };

        return new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["summary"] = new JsonObject { ["summary"] = tailored.Summary },
                ["experience"] = experience,
                ["skills"] = skills,
            },
        };
    }
}
