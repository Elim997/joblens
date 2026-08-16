using Microsoft.Extensions.AI;

namespace JobLens.Core.Llm;

public interface IScoringChatClient
{
    IChatClient ChatClient { get; }
}

public sealed class ScoringChatClient(IChatClient chatClient) : IScoringChatClient
{
    public IChatClient ChatClient { get; } = chatClient;
}
