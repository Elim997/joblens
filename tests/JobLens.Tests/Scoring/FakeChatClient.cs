using Microsoft.Extensions.AI;

namespace JobLens.Tests.Scoring;

// Returns canned response text under test control instead of calling a real model.
// Each call dequeues the next configured response; extra calls beyond what was
// configured return "[]" so an unexpected retry doesn't throw from the fake itself.
public class FakeChatClient(params string[] responses) : IChatClient
{
    private readonly Queue<string> _responses = new(responses);

    public int CallCount { get; private set; }

    public List<IEnumerable<ChatMessage>> Requests { get; } = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        CallCount++;
        Requests.Add(messages);
        var text = _responses.Count > 0 ? _responses.Dequeue() : "[]";
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("GeminiRelevanceScorer does not use streaming.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
