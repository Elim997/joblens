using JobLens.Core.Resume;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Authentication;

namespace JobLens.Tests.Resume;

public class RealResumeClientTests
{
    private sealed class FakeTokenCache(TokenContainer? tokens) : ITokenCache
    {
        public ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(tokens);

        public ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ListResumesAsync_ThrowsReziAuthenticationRequired_WhenNoTokenCached()
    {
        await using var client = new RealResumeClient(new FakeTokenCache(null), NullLogger<RealResumeClient>.Instance);

        await Assert.ThrowsAsync<ReziAuthenticationRequiredException>(() => client.ListResumesAsync());
    }

    [Fact]
    public async Task ListResumesAsync_ThrowsReziAuthenticationRequired_WhenCachedTokenIsExpired()
    {
        var expired = new TokenContainer
        {
            TokenType = "bearer",
            AccessToken = "expired-token",
            ExpiresIn = 60,
            ObtainedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(1),
        };
        await using var client = new RealResumeClient(new FakeTokenCache(expired), NullLogger<RealResumeClient>.Instance);

        await Assert.ThrowsAsync<ReziAuthenticationRequiredException>(() => client.ListResumesAsync());
    }

    [Fact]
    public void ReziAuthenticationRequiredException_MessageNamesTheReloginCommand()
    {
        var ex = new ReziAuthenticationRequiredException();
        Assert.Contains("dotnet run --project tools/ReziLogin", ex.Message);
    }
}
