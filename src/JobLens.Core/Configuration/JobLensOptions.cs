namespace JobLens.Core.Configuration;

public class JobLensOptions
{
    public string MessagesDbPath { get; set; } = "";
    public string GroupChatJid { get; set; } = "";
    public string[] TargetCategories { get; set; } = [];
    public string Profile { get; set; } = "";
    public int ScoringTopK { get; set; } = 10;
}
