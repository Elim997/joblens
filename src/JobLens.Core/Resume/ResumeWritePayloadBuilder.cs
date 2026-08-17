using System.Text.Json.Nodes;

namespace JobLens.Core.Resume;

/// <summary>
/// Builds the JSON payload IResumeClient.WriteResumeAsync deep-merges into Rezi:ForEditResumeId.
/// Takes the tailored content's discrete fields rather than a ValidatedTailoredResume, since its
/// one caller (TailoredDraftExporter) builds this from a persisted TailoredDraft, not a fresh
/// tailoring result.
/// </summary>
public static class ResumeWritePayloadBuilder
{
    public static JsonNode Build(
        string summary, IReadOnlyList<TailoredExperienceItem> experience, IReadOnlyList<TailoredSkillItem> skills)
    {
        var experienceNode = new JsonObject();
        foreach (var item in experience)
            experienceNode[item.ItemId] = new JsonObject { ["description"] = item.Description };

        var skillsNode = new JsonObject();
        foreach (var item in skills)
            skillsNode[item.ItemId] = new JsonObject { ["skill"] = item.Skill };

        return new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["summary"] = new JsonObject { ["summary"] = summary },
                ["experience"] = experienceNode,
                ["skills"] = skillsNode,
            },
        };
    }
}
