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
    public static TailorResponse From(ValidatedTailoredResume tailored, bool committed, string? writtenToResumeId) => new(
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

        // Validate the write destination before the (expensive) tailoring model call and Rezi
        // base reads, so a misconfigured ForEditResumeId fails fast instead of after burning a
        // model call. The model itself never sees or influences this value - it comes only from
        // configuration, resolved here, once, before TailorAsync runs at all.
        var forEditId = commit ? ValidateWriteDestination() : null;

        var tailored = await tailor.TailorAsync(posting, cancellationToken);

        if (!commit)
        {
            // Defense in depth: re-check the validator-produced result immediately before it
            // becomes a preview response, even though TailorAsync can only ever return an
            // already-validated result.
            ResumeTailoringValidator.ValidateComplete(tailored);
            return TailorResponse.From(tailored, committed: false, writtenToResumeId: null);
        }

        // Re-checked again immediately before building the write payload, and again immediately
        // before the write itself - both are cheap and pure, and either one failing must produce
        // zero calls to WriteResumeAsync.
        ResumeTailoringValidator.ValidateComplete(tailored);
        var payload = BuildWritePayload(tailored);

        ResumeTailoringValidator.ValidateComplete(tailored);
        await resumeClient.WriteResumeAsync(forEditId!, payload, cancellationToken);
        return TailorResponse.From(tailored, committed: true, writtenToResumeId: forEditId);
    }

    /// <summary>
    /// HARD GUARDRAIL: the only ever write target is the configured "for edit" slot - never a
    /// base, and never anything the tailoring model can influence (TailorAsync only ever reads
    /// bases; it never produces a write target). Trims the configured id and compares it against
    /// every configured base resume id ordinally, ignoring case, so a base can never become
    /// writable via /tailor through a config typo or case mismatch either.
    /// </summary>
    private string ValidateWriteDestination()
    {
        var forEditId = options.Value.ForEditResumeId?.Trim() ?? "";
        if (forEditId.Length == 0)
            throw new ResumeWriteConfigurationException("Rezi:ForEditResumeId is not configured; refusing to write.");

        var matchesBaseId = options.Value.BaseResumes.Any(b =>
            string.Equals(b.Id?.Trim(), forEditId, StringComparison.OrdinalIgnoreCase));
        if (matchesBaseId)
            throw new ResumeWriteConfigurationException(
                "Rezi:ForEditResumeId matches a configured base resume id. Refusing to write - " +
                "a base must never be writable via /tailor.");

        return forEditId;
    }

    private static JsonNode BuildWritePayload(ValidatedTailoredResume tailored)
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
