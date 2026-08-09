namespace JobLens.Core.Configuration;

public class JobLensOptions
{
    public string MessagesDbPath { get; set; } = "";
    public string GroupChatJid { get; set; } = "";
    public string[] TargetCategories { get; set; } = [];
}
