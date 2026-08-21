using System.Runtime.Versioning;
using JobLens.Core.Resume;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Authentication;

namespace JobLens.Tests.Resume;

[SupportedOSPlatform("windows")]
public class EncryptedFileTokenCacheTests : IDisposable
{
    private readonly string path = Path.Combine(
        Path.GetTempPath(),
        $"joblens-token-cache-{Guid.NewGuid():N}.json.dpapi");

    [Fact]
    public async Task InspectAsync_MissingFile_ReturnsAbsent()
    {
        var cache = CreateCache();

        var inspection = await cache.InspectAsync(CancellationToken.None);

        Assert.Equal(ReziTokenCacheStatus.Absent, inspection.Status);
        Assert.Null(inspection.Exception);
    }

    [Fact]
    public async Task InspectAsync_InvalidCiphertext_ReturnsUndecryptable()
    {
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        var cache = CreateCache();

        var inspection = await cache.InspectAsync(CancellationToken.None);

        Assert.Equal(ReziTokenCacheStatus.Undecryptable, inspection.Status);
        Assert.NotNull(inspection.Exception);
    }


    [Fact]
    public async Task InspectAsync_ValidEncryptedTokens_ReturnsPresent()
    {
        var cache = CreateCache();
        await cache.StoreTokensAsync(
            new TokenContainer
            {
                TokenType = "bearer",
                AccessToken = "cached-token",
                ExpiresIn = 3600,
                ObtainedAt = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);

        var inspection = await cache.InspectAsync(CancellationToken.None);

        Assert.Equal(ReziTokenCacheStatus.Present, inspection.Status);
        Assert.Null(inspection.Exception);
    }

    [Fact]
    public async Task GetTokensAsync_InvalidCiphertext_PreservesFailSoftNullBehavior()
    {
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        var cache = CreateCache();

        var tokens = await cache.GetTokensAsync(CancellationToken.None);

        Assert.Null(tokens);
    }

    private EncryptedFileTokenCache CreateCache() =>
        new(path, NullLogger<EncryptedFileTokenCache>.Instance);

    public void Dispose()
    {
        if (File.Exists(path))
            File.Delete(path);
        if (File.Exists(path + ".tmp"))
            File.Delete(path + ".tmp");
    }
}
