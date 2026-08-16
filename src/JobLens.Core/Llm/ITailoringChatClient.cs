using Microsoft.Extensions.AI;

namespace JobLens.Core.Llm;

public interface ITailoringChatClient
{
    IChatClient ChatClient { get; }
}

public sealed class TailoringChatClient(IChatClient chatClient) : ITailoringChatClient
{
    public IChatClient ChatClient { get; } = chatClient;
}
