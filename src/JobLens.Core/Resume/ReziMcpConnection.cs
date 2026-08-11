using Microsoft.Extensions.Logging;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;

namespace JobLens.Core.Resume;

/// <summary>
/// Connection parameters shared by RealResumeClient (the app's runtime client, which refuses
/// to trigger an interactive login) and the ReziLogin tool (which performs the interactive
/// login). Keeping them in one place means both always target the same registered OAuth
/// client and negotiate the same protocol version.
/// </summary>
public static class ReziMcpConnection
{
    public const string ServerUrl = "https://api.rezi.ai/mcp";

    // Registered once by hand (see spikes/rezi-mcp-auth-spike): the C# MCP SDK's dynamic
    // client registration hardcodes token_endpoint_auth_method=client_secret_post, but Rezi's
    // authorization server only accepts "none" (public client, PKCE-only). Pinning this
    // pre-registered client_id skips the SDK's registration path entirely. Not a secret -
    // it's a public-client identifier with no client_secret behind it.
    public const string ClientId = "afa47473-5533-4643-a5f2-4ce1333f98c6";

    // Rezi's authorization server speaks this MCP protocol revision. Pinning it (rather than
    // taking the SDK's default) also skips a separate "server/discover" probe that otherwise
    // triggers its own OAuth challenge before "initialize" triggers a second one - halving the
    // number of interactive logins needed.
    public const string ProtocolVersion = "2025-03-26";

    public static readonly Uri RedirectUri = new("http://localhost:1179/callback");

    /// <summary>
    /// Default path for the encrypted token cache, shared by RealResumeClient (reads it on
    /// every call) and the ReziLogin tool (writes it after a successful interactive sign-in).
    /// Under LocalApplicationData rather than the repo so it's never at risk of being committed.
    /// </summary>
    public static string DefaultTokenCachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JobLens", "rezi-token.dat");

    public static HttpClientTransport CreateTransport(
        HttpClient httpClient,
        ITokenCache tokenCache,
        Func<AuthorizationCallbackContext, CancellationToken, Task<AuthorizationResult?>> authorizationCallbackHandler,
        ILoggerFactory? loggerFactory = null) =>
        new(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(ServerUrl),
                Name = "JobLens Rezi Client",
                OAuth = new ClientOAuthOptions
                {
                    RedirectUri = RedirectUri,
                    ClientId = ClientId,
                    AuthorizationCallbackHandler = authorizationCallbackHandler,
                    TokenCache = tokenCache,
                },
            },
            httpClient,
            loggerFactory);
}
