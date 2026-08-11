using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace JobLens.Core.Resume;

/// <summary>
/// IResumeClient over a real Rezi MCP connection. Never triggers an interactive OAuth flow
/// itself - see ReziAuthenticationRequiredException. Connects lazily on first use and reuses
/// the same session across calls; the SDK re-reads ITokenCache before every actual request
/// (proven live in the Phase 0 spike), so a token refreshed by tools/ReziLogin while this
/// service keeps running is picked up on the very next call, no restart needed.
/// </summary>
public sealed class RealResumeClient(ITokenCache tokenCache, ILogger<RealResumeClient> logger)
    : IResumeClient, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Rezi rate-limits its endpoints (observed 429s against /oauth/authorize during the Phase 0
    // spike), so tool-call retries are capped and back off rather than hammering the server.
    private const int MaxAttempts = 3;
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];

    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private McpClient? _client;

    public async Task<IReadOnlyList<ResumeSummary>> ListResumesAsync(CancellationToken cancellationToken = default)
    {
        var node = await CallToolAsync("list_resumes", new Dictionary<string, object?>(), cancellationToken);
        return node.Deserialize<List<ResumeSummary>>(JsonOptions) ?? [];
    }

    public Task<JsonNode?> ReadResumeAsync(string resumeId, CancellationToken cancellationToken = default) =>
        CallToolAsync("read_resume", new Dictionary<string, object?> { ["resume_id"] = resumeId }, cancellationToken);

    public Task<JsonNode?> WriteResumeAsync(string resumeId, JsonNode resume, CancellationToken cancellationToken = default) =>
        CallToolAsync(
            "write_resume",
            new Dictionary<string, object?> { ["resume_id"] = resumeId, ["resume"] = resume },
            cancellationToken);

    public Task<JsonNode?> GetResumeFormatAsync(CancellationToken cancellationToken = default) =>
        CallToolAsync("get_resume_format", new Dictionary<string, object?>(), cancellationToken);

    private async Task<JsonNode?> CallToolAsync(
        string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var client = await GetClientAsync(cancellationToken);
                var result = await client.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken);
                var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
                return text is null ? null : JsonNode.Parse(text);
            }
            catch (HttpRequestException ex) when (
                ex.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
                && attempt < MaxAttempts)
            {
                var delay = RetryDelays[attempt - 1];
                logger.LogWarning(
                    "Rezi tool call '{Tool}' hit {Status} (attempt {Attempt}/{Max}); retrying in {Delay}.",
                    toolName, ex.StatusCode, attempt, MaxAttempts, delay);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private async Task<McpClient> GetClientAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
            return _client;

        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            _client ??= await ConnectAsync(cancellationToken);
            return _client;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task<McpClient> ConnectAsync(CancellationToken cancellationToken)
    {
        // Fail fast, before any network call, when there's no valid cached token - this is the
        // common case (never logged in yet, or the ~30-day token expired) and is what turns a
        // raw 401/McpException deep in the SDK into the actionable message callers actually see.
        var cached = await tokenCache.GetTokensAsync(cancellationToken);
        if (cached is null || IsExpired(cached))
            throw new ReziAuthenticationRequiredException();

        var httpClient = new HttpClient();
        var transport = ReziMcpConnection.CreateTransport(httpClient, tokenCache, RefuseInteractiveLoginAsync);
        return await McpClient.CreateAsync(
            transport,
            clientOptions: new() { ProtocolVersion = ReziMcpConnection.ProtocolVersion },
            cancellationToken: cancellationToken);
    }

    private static bool IsExpired(TokenContainer tokens) =>
        tokens.ExpiresIn is { } seconds && DateTimeOffset.UtcNow >= tokens.ObtainedAt.AddSeconds(seconds);

    // Safety net only: ConnectAsync's pre-check means this should never actually run in
    // practice - it would only fire if Rezi revokes a token server-side before our local
    // ExpiresIn clock says it should. Never opens a browser mid-request; fails clearly instead.
    private static Task<AuthorizationResult?> RefuseInteractiveLoginAsync(
        AuthorizationCallbackContext context, CancellationToken cancellationToken) =>
        throw new ReziAuthenticationRequiredException();

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
            await _client.DisposeAsync();
    }
}
