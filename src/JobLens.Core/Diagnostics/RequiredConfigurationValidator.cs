using JobLens.Core.Configuration;
using Microsoft.Extensions.Configuration;

namespace JobLens.Core.Diagnostics;

/// <summary>
/// Shared validation for every JobLens entry point. It is deliberately a pure function of the
/// fully built configuration so HTTP startup and command-line preflight cannot drift apart.
/// </summary>
public static class RequiredConfigurationValidator
{
    public static void Validate(IConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config["JobLens:MessagesDbPath"]))
            throw new InvalidOperationException("Missing JobLens:MessagesDbPath");
        if (config.GetSection("JobLens:GroupChatJids").Get<string[]>() is not { Length: > 0 })
            throw new InvalidOperationException("Missing JobLens:GroupChatJids (must be a non-empty array)");

        var scoringTemplates = config.GetSection("JobLens:ScoringTemplates").Get<ScoringTemplateOptions[]>();
        if (scoringTemplates is not { Length: > 0 })
            throw new InvalidOperationException("Missing JobLens:ScoringTemplates (must be a non-empty array)");
        if (scoringTemplates.Any(t => string.IsNullOrWhiteSpace(t.Name) || string.IsNullOrWhiteSpace(t.Profile)))
        {
            throw new InvalidOperationException(
                "Each JobLens:ScoringTemplates entry must have a non-empty Name and Profile");
        }
        if (scoringTemplates.Select(t => t.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            scoringTemplates.Length)
        {
            throw new InvalidOperationException("JobLens:ScoringTemplates names must be unique");
        }

        var defaults = new JobLensOptions();
        var matchThreshold = config.GetValue("JobLens:MatchThreshold", defaults.MatchThreshold);
        var autoTailorThreshold = config.GetValue("JobLens:AutoTailorThreshold", defaults.AutoTailorThreshold);
        var bridgeHealthPort = config.GetValue("JobLens:BridgeHealthPort", defaults.BridgeHealthPort);

        if (autoTailorThreshold < 0 || autoTailorThreshold > 100)
        {
            throw new InvalidOperationException(
                "JobLens:AutoTailorThreshold must be between 0 and 100 (the valid relevance score range)");
        }
        if (autoTailorThreshold < matchThreshold)
        {
            throw new InvalidOperationException(
                "JobLens:AutoTailorThreshold must be greater than or equal to JobLens:MatchThreshold");
        }
        if (bridgeHealthPort is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                "JobLens:BridgeHealthPort must be between 1 and 65535");
        }

        if (string.IsNullOrWhiteSpace(config["Postgres:ConnectionString"]))
            throw new InvalidOperationException("Missing Postgres:ConnectionString");
        if (string.IsNullOrWhiteSpace(config["Gemini:ApiKey"]))
            throw new InvalidOperationException("Missing Gemini:ApiKey");

        var llmBaseUrl = config["Llm:BaseUrl"];
        if (string.IsNullOrWhiteSpace(llmBaseUrl))
            throw new InvalidOperationException("Missing Llm:BaseUrl");
        if (!Uri.TryCreate(llmBaseUrl, UriKind.Absolute, out var llmUri) ||
            (llmUri.Scheme != Uri.UriSchemeHttp && llmUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Llm:BaseUrl must be an absolute HTTP or HTTPS URI");
        }

        if (string.IsNullOrWhiteSpace(config["Llm:ApiKey"]))
            throw new InvalidOperationException("Missing Llm:ApiKey");

        var scoringModel = config["Llm:ScoringModel"];
        if (string.IsNullOrWhiteSpace(scoringModel))
            throw new InvalidOperationException("Missing Llm:ScoringModel");

        var tailoringModel = config["Llm:TailoringModel"];
        if (string.IsNullOrWhiteSpace(tailoringModel))
            throw new InvalidOperationException("Missing Llm:TailoringModel");

        if (string.Equals(scoringModel.Trim(), "coding-fallback", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(tailoringModel.Trim(), scoringModel.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Llm:TailoringModel must be a dedicated model, not the scoring fallback combo");
        }
    }
}
