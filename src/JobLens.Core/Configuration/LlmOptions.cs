namespace JobLens.Core.Configuration;

public class LlmOptions
{
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string ScoringModel { get; set; } = "";
    public string TailoringModel { get; set; } = "";
}
