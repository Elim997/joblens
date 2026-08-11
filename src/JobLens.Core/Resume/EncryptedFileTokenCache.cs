using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Authentication;

namespace JobLens.Core.Resume;

/// <summary>
/// Persists the Rezi OAuth token across process restarts so a headless service reuses the
/// ~30-day access token instead of needing an interactive login on every start. Encrypted at
/// rest with DPAPI (CurrentUser scope): only the same Windows user account can decrypt it.
/// GetTokensAsync re-reads the file on every call (per ITokenCache's contract), so running
/// tools/ReziLogin while the service is running is picked up by the very next request.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EncryptedFileTokenCache(string path, ILogger<EncryptedFileTokenCache> logger) : ITokenCache
{
    private static readonly byte[] Entropy = "JobLens.Resume.EncryptedFileTokenCache"u8.ToArray();

    public async ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.SerializeToUtf8Bytes(tokens);
        var encrypted = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);

        // Write to a temp file then move into place so a crash mid-write can never leave a
        // corrupt cache file that GetTokensAsync would fail to parse.
        var tempPath = path + ".tmp";
        await File.WriteAllBytesAsync(tempPath, encrypted, cancellationToken);
        File.Move(tempPath, path, overwrite: true);

        logger.LogInformation("Rezi token saved to {Path} (expires in {ExpiresIn}s).", path, tokens.ExpiresIn);
    }

    public async ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var encrypted = await File.ReadAllBytesAsync(path, cancellationToken);
            var json = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<TokenContainer>(json);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            // Corrupt cache file, or encrypted under a different Windows user account -
            // treat as "no cached token" rather than crash; re-running tools/ReziLogin
            // regenerates it (see ReziAuthenticationRequiredException for the guidance callers see).
            logger.LogWarning(ex, "Could not read Rezi token cache at {Path}; treating as absent.", path);
            return null;
        }
    }
}
