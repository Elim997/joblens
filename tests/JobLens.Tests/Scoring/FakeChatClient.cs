using Microsoft.Extensions.AI;

namespace JobLens.Tests.Scoring;

// Returns canned response text or exceptions under test control instead of calling a real model.
// Each call dequeues the next configured outcome; extra calls beyond what was configured
// return "[]" so an unexpected retry does not throw from the fake itself.
public class FakeChatClient(params object[] outcomes) : IChatClient
{
    private readonly Queue<object> _outcomes = new(outcomes);

    public int CallCount { get; private set; }

    public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

    public List<ChatOptions?> Options { get; } = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        Requests.Add(messages.ToList());
        Options.Add(options);
        cancellationToken.ThrowIfCancellationRequested();

        var outcome = _outcomes.Count > 0 ? _outcomes.Dequeue() : "[]";
        return outcome switch
        {
            string text => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text))),
            Exception exception => Task.FromException<ChatResponse>(exception),
            _ => Task.FromException<ChatResponse>(new InvalidOperationException(
                $"Unsupported fake chat outcome type '{outcome.GetType().Name}'.")),
        };
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("JobLens chat clients do not use streaming.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
