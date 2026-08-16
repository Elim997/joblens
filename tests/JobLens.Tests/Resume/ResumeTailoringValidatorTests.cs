using JobLens.Core.Resume;

namespace JobLens.Tests.Resume;

/// <summary>
/// Exhaustive coverage of ResumeTailoringValidator.Validate - a pure function with no
/// network/OmniRoute/LLM/Rezi/HTTP dependency, so every case here is a plain (input, expected
/// output-or-exception) check.
/// </summary>
public class ResumeTailoringValidatorTests
{
    private static readonly BaseSelection Selection = new("base-id", "QA Automation Developer", "Matches the posting.");

    private static BaseResumeSnapshot MakeSnapshot(
        string summary = "Base summary.",
        IReadOnlyList<ResumeItemSnapshot>? experience = null,
        IReadOnlyList<ResumeItemSnapshot>? skills = null) => new(
        Selection,
        summary,
        experience ?? [new ResumeItemSnapshot("exp1", "Base experience text."), new ResumeItemSnapshot("exp2", "Second base experience.")],
        skills ?? [new ResumeItemSnapshot("sk1", "Base skill text.")]);

    private static UntrustedRewrite MakeRewrite(
        string? summary = "Rewritten summary.",
        List<UntrustedRewriteItem?>? experience = null,
        List<UntrustedRewriteItem?>? skills = null,
        string? rationale = "Why.") => new(
        summary,
        experience ?? [],
        skills ?? [],
        rationale);

    [Fact]
    public void Validate_NullRewrite_Throws()
    {
        var ex = Assert.Throws<ResumeTailoringValidationException>(() => ResumeTailoringValidator.Validate(MakeSnapshot(), null));
        Assert.Contains("missing or unusable", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_MissingOrBlankSummary_Throws(string? summary)
    {
        var rewrite = MakeRewrite(summary: summary);
        Assert.Throws<ResumeTailoringValidationException>(() => ResumeTailoringValidator.Validate(MakeSnapshot(), rewrite));
    }

    [Fact]
    public void Validate_NullExperienceArray_Throws()
    {
        var rewrite = MakeRewrite() with { Experience = null };
        var ex = Assert.Throws<ResumeTailoringValidationException>(() => ResumeTailoringValidator.Validate(MakeSnapshot(), rewrite));
        Assert.Contains("experience array", ex.Message);
    }

    [Fact]
    public void Validate_NullSkillsArray_Throws()
    {
        var rewrite = MakeRewrite() with { Skills = null };
        var ex = Assert.Throws<ResumeTailoringValidationException>(() => ResumeTailoringValidator.Validate(MakeSnapshot(), rewrite));
        Assert.Contains("skills array", ex.Message);
    }

    [Fact]
    public void Validate_ExperienceArrayContainsNullEntry_Throws()
    {
        var rewrite = MakeRewrite(experience: [null]);
        var ex = Assert.Throws<ResumeTailoringValidationException>(() => ResumeTailoringValidator.Validate(MakeSnapshot(), rewrite));
        Assert.Contains("null entry", ex.Message);
    }

    [Fact]
    public void Validate_SkillsArrayContainsNullEntry_Throws()
    {
        var rewrite = MakeRewrite(skills: [null]);
        Assert.Throws<ResumeTailoringValidationException>(() => ResumeTailoringValidator.Validate(MakeSnapshot(), rewrite));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Validate_ExperienceItemBlankId_Throws(string? itemId)
    {
        var rewrite = MakeRewrite(experience: [new UntrustedRewriteItem(itemId, "text", null)]);
        var ex = Assert.Throws<ResumeTailoringValidationException>(() => ResumeTailoringValidator.Validate(MakeSnapshot(), rewrite));
        Assert.Contains("blank id", ex.Message);
    }

    [Fact]
    public void Validate_ExperienceDuplicateId_Throws()
    {
        var rewrite = MakeRewrite(experience:
        [
            new UntrustedRewriteItem("exp1", "First edit.", null),
            new UntrustedRewriteItem("exp1", "Second edit.", null),
        ]);
        var ex = Assert.Throws<ResumeTailoringValidationException>(() => ResumeTailoringValidator.Validate(MakeSnapshot(), rewrite));
        Assert.Contains("duplicate id", ex.Message);
    }

    [Fact]
    public void Validate_SkillsDuplicateId_Throws()
    {
        var rewrite = MakeRewrite(skills:
        [
            new UntrustedRewriteItem("sk1", null, "First edit."),
            new UntrustedRewriteItem("sk1", null, "Second edit."),
        ]);
        Assert.Throws<ResumeTailoringValidationException>(() => ResumeTailoringValidator.Validate(MakeSnapshot(), rewrite));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Validate_SkillItemBlankId_Throws(string? itemId)
    {
        var rewrite = MakeRewrite(skills: [new UntrustedRewriteItem(itemId, null, "text")]);
        var ex = Assert.Throws<ResumeTailoringValidationException>(
            () => ResumeTailoringValidator.Validate(MakeSnapshot(), rewrite));
        Assert.Contains("blank id", ex.Message);
    }

    [Fact]
    public void Validate_ExperienceInventedId_Throws()
    {
        var rewrite = MakeRewrite(experience: [new UntrustedRewriteItem("made-up-id", "Should not be accepted.", null)]);
        var ex = Assert.Throws<ResumeTailoringValidationException>(() => ResumeTailoringValidator.Validate(MakeSnapshot(), rewrite));
        Assert.Contains("unknown id", ex.Message);
    }

    [Fact]
    public void Validate_SkillsInventedId_Throws()
    {
        var rewrite = MakeRewrite(skills: [new UntrustedRewriteItem("made-up-id", null, "Should not be accepted.")]);
        Assert.Throws<ResumeTailoringValidationException>(() => ResumeTailoringValidator.Validate(MakeSnapshot(), rewrite));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_ExperienceBlankText_Throws(string? text)
    {
        var rewrite = MakeRewrite(experience: [new UntrustedRewriteItem("exp1", text, null)]);
        var ex = Assert.Throws<ResumeTailoringValidationException>(() => ResumeTailoringValidator.Validate(MakeSnapshot(), rewrite));
        Assert.Contains("blank text", ex.Message);
    }

    [Fact]
    public void Validate_SkillBlankText_Throws()
    {
        var rewrite = MakeRewrite(skills: [new UntrustedRewriteItem("sk1", null, "  ")]);
        Assert.Throws<ResumeTailoringValidationException>(() => ResumeTailoringValidator.Validate(MakeSnapshot(), rewrite));
    }

    [Fact]
    public void Validate_ExperienceItemFillsWrongField_TreatedAsBlank()
    {
        // "skill" is filled but this is an experience item read via Description - the wrong-field
        // value must not be silently accepted from the other field.
        var rewrite = MakeRewrite(experience: [new UntrustedRewriteItem("exp1", null, "Filled in the wrong field.")]);
        Assert.Throws<ResumeTailoringValidationException>(() => ResumeTailoringValidator.Validate(MakeSnapshot(), rewrite));
    }

    [Fact]
    public void Validate_FullValidRewrite_ReturnsMergedResult()
    {
        var rewrite = MakeRewrite(
            summary: "  Rewritten summary.  ",
            experience:
            [
                new UntrustedRewriteItem("exp1", "New exp1 text.", null),
                new UntrustedRewriteItem("exp2", "New exp2 text.", null),
            ],
            skills: [new UntrustedRewriteItem("sk1", null, "New sk1 text.")],
            rationale: "  Emphasized fit.  ");

        var result = ResumeTailoringValidator.Validate(MakeSnapshot(), rewrite);

        Assert.Equal("Rewritten summary.", result.Summary);
        Assert.Equal("Emphasized fit.", result.RewriteRationale);
        Assert.Equal(Selection, result.BaseSelection);

        Assert.Equal(2, result.Experience.Count);
        Assert.Equal("exp1", result.Experience[0].ItemId);
        Assert.Equal("New exp1 text.", result.Experience[0].Description);
        Assert.Equal("exp2", result.Experience[1].ItemId);
        Assert.Equal("New exp2 text.", result.Experience[1].Description);

        Assert.Single(result.Skills);
        Assert.Equal("sk1", result.Skills[0].ItemId);
        Assert.Equal("New sk1 text.", result.Skills[0].Skill);
    }

    [Fact]
    public void Validate_PartialRewrite_OmittedIdKeepsOriginalText()
    {
        var rewrite = MakeRewrite(experience: [new UntrustedRewriteItem("exp1", "New exp1 text.", null)]);

        var result = ResumeTailoringValidator.Validate(MakeSnapshot(), rewrite);

        Assert.Equal(2, result.Experience.Count);
        Assert.Equal("exp1", result.Experience[0].ItemId);
        Assert.Equal("New exp1 text.", result.Experience[0].Description);
        // exp2 was never mentioned in the rewrite - original text must survive untouched.
        Assert.Equal("exp2", result.Experience[1].ItemId);
        Assert.Equal("Second base experience.", result.Experience[1].Description);
    }

    [Fact]
    public void Validate_EmptyRewriteArrays_PreservesAllOriginalItemsUnchanged()
    {
        var snapshot = MakeSnapshot();
        var rewrite = MakeRewrite(experience: [], skills: []);

        var result = ResumeTailoringValidator.Validate(snapshot, rewrite);

        Assert.Equal(snapshot.Experience.Select(i => i.ItemId), result.Experience.Select(i => i.ItemId));
        Assert.Equal(snapshot.Experience.Select(i => i.Text), result.Experience.Select(i => i.Description));
        Assert.Equal(snapshot.Skills.Select(i => i.ItemId), result.Skills.Select(i => i.ItemId));
        Assert.Equal(snapshot.Skills.Select(i => i.Text), result.Skills.Select(i => i.Skill));
    }

    [Fact]
    public void Validate_MergedResult_ContainsEveryOriginalIdExactlyOnce_Experience()
    {
        var snapshot = MakeSnapshot();
        var rewrite = MakeRewrite(experience: [new UntrustedRewriteItem("exp2", "Only exp2 rewritten.", null)]);

        var result = ResumeTailoringValidator.Validate(snapshot, rewrite);

        Assert.Equal(snapshot.Experience.Select(i => i.ItemId), result.Experience.Select(i => i.ItemId));
        Assert.Equal(result.Experience.Select(i => i.ItemId), result.Experience.Select(i => i.ItemId).Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public void Validate_MergedResult_ContainsEveryOriginalIdExactlyOnce_Skills()
    {
        var snapshot = MakeSnapshot(skills: [new ResumeItemSnapshot("sk1", "t1"), new ResumeItemSnapshot("sk2", "t2"), new ResumeItemSnapshot("sk3", "t3")]);
        var rewrite = MakeRewrite(skills: [new UntrustedRewriteItem("sk2", null, "New sk2.")]);

        var result = ResumeTailoringValidator.Validate(snapshot, rewrite);

        Assert.Equal(["sk1", "sk2", "sk3"], result.Skills.Select(i => i.ItemId));
        Assert.Equal(result.Skills.Select(i => i.ItemId), result.Skills.Select(i => i.ItemId).Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public void Validate_ReturnedIdsAreSubsetOfBaseIds_NeverAddsNewOnes()
    {
        var snapshot = MakeSnapshot();
        var rewrite = MakeRewrite(experience: [new UntrustedRewriteItem("exp1", "New text.", null)]);

        var result = ResumeTailoringValidator.Validate(snapshot, rewrite);

        Assert.All(result.Experience, item => Assert.Contains(item.ItemId, snapshot.Experience.Select(i => i.ItemId)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_MissingOrBlankRationale_Throws(string? rationale)
    {
        var rewrite = MakeRewrite(rationale: rationale);

        var ex = Assert.Throws<ResumeTailoringValidationException>(
            () => ResumeTailoringValidator.Validate(MakeSnapshot(), rewrite));

        Assert.Contains("rationale", ex.Message);
    }

    [Fact]
    public void Validate_OriginalIdsOnResult_MatchBaseSnapshotExactly()
    {
        var snapshot = MakeSnapshot();
        var rewrite = MakeRewrite();

        var result = ResumeTailoringValidator.Validate(snapshot, rewrite);

        Assert.Equal(["exp1", "exp2"], result.OriginalExperienceIds);
        Assert.Equal(["sk1"], result.OriginalSkillIds);
    }

    [Fact]
    public void Validate_ItemIdComparisonIsOrdinal_CaseSensitive()
    {
        // "EXP1" differs from the base's "exp1" under ordinal comparison, so it must be rejected
        // as an unknown id rather than matched case-insensitively.
        var rewrite = MakeRewrite(experience: [new UntrustedRewriteItem("EXP1", "text", null)]);
        var ex = Assert.Throws<ResumeTailoringValidationException>(() => ResumeTailoringValidator.Validate(MakeSnapshot(), rewrite));
        Assert.Contains("unknown id", ex.Message);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(true, "")]
    [InlineData(true, "   ")]
    [InlineData(false, null)]
    [InlineData(false, "")]
    [InlineData(false, "   ")]
    public void Validate_BaseSectionBlankId_Throws(bool experienceSection, string? itemId)
    {
        var items = new[] { new ResumeItemSnapshot(itemId ?? "", "Base text.") };
        var snapshot = experienceSection
            ? MakeSnapshot(experience: items)
            : MakeSnapshot(skills: items);

        var ex = Assert.Throws<ResumeTailoringValidationException>(
            () => ResumeTailoringValidator.Validate(snapshot, MakeRewrite()));

        Assert.Contains("blank id", ex.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Validate_BaseSectionDuplicateId_Throws(bool experienceSection)
    {
        ResumeItemSnapshot[] items =
        [
            new("same-id", "First base text."),
            new("same-id", "Second base text."),
        ];
        var snapshot = experienceSection
            ? MakeSnapshot(experience: items)
            : MakeSnapshot(skills: items);

        var ex = Assert.Throws<ResumeTailoringValidationException>(
            () => ResumeTailoringValidator.Validate(snapshot, MakeRewrite()));

        Assert.Contains("duplicate", ex.Message);
    }

    [Fact]
    public void Validate_UntrustedIdsAreNotIncludedInExceptionMessages()
    {
        const string modelReturnedId = "sensitive-model-fragment";
        var rewrite = MakeRewrite(
            experience: [new UntrustedRewriteItem(modelReturnedId, "text", null)]);

        var ex = Assert.Throws<ResumeTailoringValidationException>(
            () => ResumeTailoringValidator.Validate(MakeSnapshot(), rewrite));

        Assert.DoesNotContain(modelReturnedId, ex.Message);
    }

    [Fact]
    public void Validate_ResultCollectionsAreDefensiveCopiesAndReadOnly()
    {
        var experience = new List<ResumeItemSnapshot> { new("exp1", "Base experience.") };
        var skills = new List<ResumeItemSnapshot> { new("sk1", "Base skill.") };
        var snapshot = MakeSnapshot(experience: experience, skills: skills);
        var result = ResumeTailoringValidator.Validate(snapshot, MakeRewrite());

        experience[0] = new ResumeItemSnapshot("changed-exp", "Changed.");
        skills[0] = new ResumeItemSnapshot("changed-skill", "Changed.");

        Assert.Equal("exp1", Assert.Single(result.Experience).ItemId);
        Assert.Equal("sk1", Assert.Single(result.Skills).ItemId);
        Assert.Equal("exp1", Assert.Single(result.OriginalExperienceIds));
        Assert.Equal("sk1", Assert.Single(result.OriginalSkillIds));
        Assert.Throws<NotSupportedException>(
            () => ((IList<TailoredExperienceItem>)result.Experience)[0] = new("changed", "Changed."));
        Assert.Throws<NotSupportedException>(
            () => ((IList<TailoredSkillItem>)result.Skills)[0] = new("changed", "Changed."));
        Assert.Throws<NotSupportedException>(
            () => ((IList<string>)result.OriginalExperienceIds)[0] = "changed");
        Assert.Throws<NotSupportedException>(
            () => ((IList<string>)result.OriginalSkillIds)[0] = "changed");
    }


    // ValidateComplete: the defense-in-depth re-check ResumeTailoringRunner runs immediately
    // before a preview response, a write payload, and the Rezi write itself. Constructs
    // ValidatedTailoredResume directly via its internal constructor (available to this test
    // assembly - see JobLens.Core.csproj's InternalsVisibleTo) to simulate a malformed result
    // reaching the final gate, independent of whether Validate() could ever actually produce one.

    private static ValidatedTailoredResume MakeComplete(
        BaseSelection? baseSelection = null,
        string summary = "Rewritten summary.",
        IReadOnlyList<TailoredExperienceItem>? experience = null,
        IReadOnlyList<TailoredSkillItem>? skills = null,
        string rewriteRationale = "Why.",
        IReadOnlyList<string>? originalExperienceIds = null,
        IReadOnlyList<string>? originalSkillIds = null) => new(
        baseSelection ?? Selection,
        summary,
        experience ?? [new TailoredExperienceItem("exp1", "Exp text.")],
        skills ?? [new TailoredSkillItem("sk1", "Skill text.")],
        rewriteRationale,
        originalExperienceIds ?? ["exp1"],
        originalSkillIds ?? ["sk1"]);

    [Fact]
    public void ValidateComplete_WellFormedResult_DoesNotThrow()
    {
        var exception = Record.Exception(() => ResumeTailoringValidator.ValidateComplete(MakeComplete()));
        Assert.Null(exception);
    }

    [Theory]
    [InlineData("", "QA Automation Developer", "Matches.")]
    [InlineData("   ", "QA Automation Developer", "Matches.")]
    [InlineData("base-id", "", "Matches.")]
    [InlineData("base-id", "QA Automation Developer", "")]
    public void ValidateComplete_BlankBaseSelectionField_Throws(string id, string name, string rationale)
    {
        var tailored = MakeComplete(baseSelection: new BaseSelection(id, name, rationale));
        Assert.Throws<ResumeTailoringValidationException>(() => ResumeTailoringValidator.ValidateComplete(tailored));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateComplete_BlankSummary_Throws(string? summary)
    {
        var tailored = MakeComplete(summary: summary!);
        Assert.Throws<ResumeTailoringValidationException>(() => ResumeTailoringValidator.ValidateComplete(tailored));
    }

    [Fact]
    public void ValidateComplete_ExperienceMissingAnOriginalId_Throws()
    {
        // Original ids say exp1+exp2 must both be covered, but only exp1 is present.
        var tailored = MakeComplete(
            experience: [new TailoredExperienceItem("exp1", "text")],
            originalExperienceIds: ["exp1", "exp2"]);

        var ex = Assert.Throws<ResumeTailoringValidationException>(() => ResumeTailoringValidator.ValidateComplete(tailored));
        Assert.Contains("experience", ex.Message);
    }

    [Fact]
    public void ValidateComplete_SkillsMissingAnOriginalId_Throws()
    {
        var tailored = MakeComplete(
            skills: [new TailoredSkillItem("sk1", "text")],
            originalSkillIds: ["sk1", "sk2"]);

        Assert.Throws<ResumeTailoringValidationException>(() => ResumeTailoringValidator.ValidateComplete(tailored));
    }

    [Fact]
    public void ValidateComplete_ExperienceDuplicateFinalId_Throws()
    {
        var tailored = MakeComplete(
            experience: [new TailoredExperienceItem("exp1", "a"), new TailoredExperienceItem("exp1", "b")],
            originalExperienceIds: ["exp1"]);

        var ex = Assert.Throws<ResumeTailoringValidationException>(() => ResumeTailoringValidator.ValidateComplete(tailored));
        Assert.Contains("duplicate", ex.Message);
    }

    [Fact]
    public void ValidateComplete_ExperienceInventedId_Throws()
    {
        // Same count as the allowed set, but one id was never on the base at all.
        var tailored = MakeComplete(
            experience: [new TailoredExperienceItem("invented-id", "text")],
            originalExperienceIds: ["exp1"]);

        var ex = Assert.Throws<ResumeTailoringValidationException>(() => ResumeTailoringValidator.ValidateComplete(tailored));
        Assert.Contains("outside the original allowed set", ex.Message);
    }

    [Fact]
    public void ValidateComplete_SkillsInventedId_Throws()
    {
        var tailored = MakeComplete(
            skills: [new TailoredSkillItem("invented-id", "text")],
            originalSkillIds: ["sk1"]);

        Assert.Throws<ResumeTailoringValidationException>(() => ResumeTailoringValidator.ValidateComplete(tailored));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateComplete_ExperienceBlankText_Throws(string? text)
    {
        var tailored = MakeComplete(experience: [new TailoredExperienceItem("exp1", text!)]);
        Assert.Throws<ResumeTailoringValidationException>(() => ResumeTailoringValidator.ValidateComplete(tailored));
    }

    [Fact]
    public void ValidateComplete_SkillBlankText_Throws()
    {
        var tailored = MakeComplete(skills: [new TailoredSkillItem("sk1", "  ")]);
        Assert.Throws<ResumeTailoringValidationException>(() => ResumeTailoringValidator.ValidateComplete(tailored));
    }
}
