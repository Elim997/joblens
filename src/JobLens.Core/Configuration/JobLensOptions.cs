namespace JobLens.Core.Configuration;

public class JobLensOptions
{
    public string MessagesDbPath { get; set; } = "";
    public string[] GroupChatJids { get; set; } = [];
    public string[] TargetCategories { get; set; } = [];

    // Scoring source of truth (replaces the old single Profile string): each posting is
    // routed locally (cosine similarity, no LLM, no Rezi read) to whichever template it's
    // closest to, then scored against that template's profile text. See TemplateCatalog.
    public List<ScoringTemplateOptions> ScoringTemplates { get; set; } = [];

    public int ScoringTopK { get; set; } = 40;
    public int MatchThreshold { get; set; } = 70;

    // A posting scoring at or above this threshold gets a TailoredDraft created automatically
    // during /run (via TailoredDraftService, the same path POST /tailor uses) - never exported
    // to Rezi automatically. Must be >= MatchThreshold; see Program.ValidateRequiredConfig.
    public int AutoTailorThreshold { get; set; } = 80;
    public int BridgeHealthPort { get; set; } = 8080;
}
