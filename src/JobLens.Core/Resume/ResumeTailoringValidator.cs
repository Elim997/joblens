namespace JobLens.Core.Resume;

/// <summary>One item (an experience bullet or a skill line) as read from a base resume, keyed by
/// its Rezi item id.</summary>
internal sealed record ResumeItemSnapshot(string ItemId, string Text);

/// <summary>
/// Trusted read of the base resume a rewrite was derived from, captured before any model call.
/// ResumeTailoringValidator checks untrusted model output against this snapshot - never against
/// a second live read - so validation itself has no network/model dependency.
/// </summary>
internal sealed record BaseResumeSnapshot(
    BaseSelection BaseSelection,
    string Summary,
    IReadOnlyList<ResumeItemSnapshot> Experience,
    IReadOnlyList<ResumeItemSnapshot> Skills);

/// <summary>
/// Pure, dependency-free validation of tailoring model output against the base resume it claims
/// to rewrite: no network, no OmniRoute, no LLM, no Rezi, no HTTP - a deterministic function of
/// (trusted base snapshot, untrusted model output) -> ValidatedTailoredResume, or a thrown
/// ResumeTailoringValidationException describing exactly what was wrong. Validate() is the only
/// way to construct a ValidatedTailoredResume, so a caller cannot hand-build one and skip these
/// checks. Item ids are compared ordinally throughout, matching how Rezi's own item keys compare.
/// </summary>
internal static class ResumeTailoringValidator
{
    public static ValidatedTailoredResume Validate(BaseResumeSnapshot baseSnapshot, UntrustedRewrite? rewrite)
    {
        if (rewrite is null)
            throw new ResumeTailoringValidationException("Tailoring rewrite response was missing or unusable.");

        if (string.IsNullOrWhiteSpace(rewrite.Summary))
            throw new ResumeTailoringValidationException("Tailoring rewrite response had a missing or blank summary.");

        if (rewrite.Experience is null)
            throw new ResumeTailoringValidationException("Tailoring rewrite response had a missing experience array.");

        if (rewrite.Skills is null)
            throw new ResumeTailoringValidationException("Tailoring rewrite response had a missing skills array.");

        if (string.IsNullOrWhiteSpace(rewrite.Rationale))
            throw new ResumeTailoringValidationException("Tailoring rewrite response had a missing or blank rationale.");

        var experience = MergeSection(baseSnapshot.Experience, rewrite.Experience, "experience", item => item.Description);
        var skills = MergeSection(baseSnapshot.Skills, rewrite.Skills, "skills", item => item.Skill);

        return new ValidatedTailoredResume(
            baseSnapshot.BaseSelection,
            rewrite.Summary.Trim(),
            experience.Select(i => new TailoredExperienceItem(i.ItemId, i.Text)).ToList(),
            skills.Select(i => new TailoredSkillItem(i.ItemId, i.Text)).ToList(),
            rewrite.Rationale.Trim(),
            baseSnapshot.Experience.Select(i => i.ItemId).ToList(),
            baseSnapshot.Skills.Select(i => i.ItemId).ToList());
    }

    /// <summary>
    /// Merges one section (experience or skills): every base item id is preserved exactly once,
    /// keeping its original text unless the rewrite supplied a valid replacement for that id.
    /// Anything the rewrite gets wrong - a null entry, a blank/missing id, a duplicate id, an id
    /// that isn't actually on the base, or blank replacement text - fails validation outright
    /// rather than being dropped or defaulted away.
    /// </summary>
    private static IReadOnlyList<ResumeItemSnapshot> MergeSection(
        IReadOnlyList<ResumeItemSnapshot> original,
        List<UntrustedRewriteItem?> rewritten,
        string sectionName,
        Func<UntrustedRewriteItem, string?> textSelector)
    {
        var knownIds = ValidateBaseIds(original, sectionName);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var rewrittenById = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var index = 0; index < rewritten.Count; index++)
        {
            var item = rewritten[index];
            if (item is null)
                throw new ResumeTailoringValidationException(
                    $"Tailoring rewrite {sectionName} array contained a null entry at position {index}.");

            if (string.IsNullOrWhiteSpace(item.ItemId))
                throw new ResumeTailoringValidationException(
                    $"Tailoring rewrite {sectionName} array contained an item with a blank id at position {index}.");

            if (!seenIds.Add(item.ItemId))
                throw new ResumeTailoringValidationException(
                    $"Tailoring rewrite {sectionName} array had a duplicate id at position {index}.");

            if (!knownIds.Contains(item.ItemId))
                throw new ResumeTailoringValidationException(
                    $"Tailoring rewrite {sectionName} array referenced an unknown id at position {index}.");

            var text = textSelector(item);
            if (string.IsNullOrWhiteSpace(text))
                throw new ResumeTailoringValidationException(
                    $"Tailoring rewrite {sectionName} item at position {index} had blank text.");

            rewrittenById[item.ItemId] = text.Trim();
        }

        // `original` is the base's exact set of item ids; a 1:1 projection over it is what
        // guarantees every original id survives in the result exactly once, whether or not the
        // rewrite touched it - omission can never drop a base item.
        var merged = original
            .Select(o => new ResumeItemSnapshot(o.ItemId, rewrittenById.GetValueOrDefault(o.ItemId, o.Text)))
            .ToList();

        var blankIndex = merged.FindIndex(i => string.IsNullOrWhiteSpace(i.Text));
        if (blankIndex >= 0)
            throw new ResumeTailoringValidationException(
                $"Base {sectionName} item at position {blankIndex} has blank text and the rewrite did not replace it.");

        return merged;
    }

    private static HashSet<string> ValidateBaseIds(
        IReadOnlyList<ResumeItemSnapshot> original,
        string sectionName)
    {
        var knownIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < original.Count; index++)
        {
            var itemId = original[index].ItemId;
            if (string.IsNullOrWhiteSpace(itemId))
                throw new ResumeTailoringValidationException(
                    $"Base {sectionName} item at position {index} has a blank id.");

            if (!knownIds.Add(itemId))
                throw new ResumeTailoringValidationException(
                    $"Base {sectionName} has a duplicate item id at position {index}.");
        }

        return knownIds;
    }


    /// <summary>
    /// Defense-in-depth re-check of an already-validated result, run again immediately before it
    /// is turned into a preview response, a write payload, or an actual Rezi write (see
    /// ResumeTailoringRunner). Validate() is the only way to produce a ValidatedTailoredResume in
    /// the first place, so this should never find anything wrong in practice - it exists so a
    /// future refactor that lets a malformed or hand-built result reach the write path fails
    /// loudly here instead of writing bad data to Rezi. Pure: no network, no Rezi, no LLM.
    /// </summary>
    public static void ValidateComplete(ValidatedTailoredResume tailored)
    {
        if (string.IsNullOrWhiteSpace(tailored.BaseSelection.BaseResumeId))
            throw new ResumeTailoringValidationException("Validated result has a blank base resume id.");
        if (string.IsNullOrWhiteSpace(tailored.BaseSelection.BaseResumeName))
            throw new ResumeTailoringValidationException("Validated result has a blank base resume name.");
        if (string.IsNullOrWhiteSpace(tailored.BaseSelection.Rationale))
            throw new ResumeTailoringValidationException("Validated result has a blank base-selection rationale.");

        if (string.IsNullOrWhiteSpace(tailored.Summary))
            throw new ResumeTailoringValidationException("Validated result has a blank summary.");

        ValidateFinalSection(tailored.OriginalExperienceIds, tailored.Experience, i => i.ItemId, i => i.Description, "experience");
        ValidateFinalSection(tailored.OriginalSkillIds, tailored.Skills, i => i.ItemId, i => i.Skill, "skills");
    }

    /// <summary>
    /// Confirms one final section (Experience or Skills) exactly covers its original allowed id
    /// set: every id present, each exactly once, nothing invented, and every item's text nonblank.
    /// </summary>
    private static void ValidateFinalSection<T>(
        IReadOnlyList<string> originalIds,
        IReadOnlyList<T> finalItems,
        Func<T, string> idSelector,
        Func<T, string> textSelector,
        string sectionName)
    {
        var allowedIds = new HashSet<string>(originalIds, StringComparer.Ordinal);
        if (allowedIds.Count != originalIds.Count)
            throw new ResumeTailoringValidationException($"Validated {sectionName} original id set contains a duplicate.");

        // No upfront finalItems.Count-vs-originalIds.Count check: it would fire before the
        // per-item duplicate/invented-id checks below and produce a less specific error for
        // the same malformed input (e.g. a duplicated id, which also makes the counts differ).
        // Every count mismatch is still caught - either by the duplicate/invented-id check
        // during the loop, or by the missing-id check once the loop completes.
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in finalItems)
        {
            var itemId = idSelector(item);
            if (string.IsNullOrWhiteSpace(itemId))
                throw new ResumeTailoringValidationException($"Validated {sectionName} item has a blank id.");

            if (!seenIds.Add(itemId))
                throw new ResumeTailoringValidationException($"Validated {sectionName} has a duplicate final id.");

            if (!allowedIds.Contains(itemId))
                throw new ResumeTailoringValidationException(
                    $"Validated {sectionName} contains an id outside the original allowed set.");

            if (string.IsNullOrWhiteSpace(textSelector(item)))
                throw new ResumeTailoringValidationException($"Validated {sectionName} item has blank text.");
        }

        if (seenIds.Count != allowedIds.Count)
            throw new ResumeTailoringValidationException($"Validated {sectionName} is missing an original allowed id.");
    }
}
