// Throwaway spike: can a headless .NET process authenticate to Rezi's MCP server
// (https://api.rezi.ai/mcp) and call list_resumes? Not wired into JobLens.
// See CLAUDE.md Phase 0 for the go/no-go question this answers.

using Microsoft.Extensions.Logging;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;

var serverUrl = "https://api.rezi.ai/mcp";

Console.WriteLine("Rezi MCP Auth Spike");
Console.WriteLine($"Connecting to {serverUrl} ...");
Console.WriteLine();

var sharedHandler = new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1)
};
var httpClient = new HttpClient(sharedHandler);

var consoleLoggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Debug);
});

var transport = new HttpClientTransport(new()
{
    Endpoint = new Uri(serverUrl),
    Name = "Rezi MCP Auth Spike",
    OAuth = new()
    {
        RedirectUri = new Uri("http://localhost:1179/callback"),
        AuthorizationCallbackHandler = HandleAuthorizationUrlAsync,
        // SDK 2.1.0 hardcodes token_endpoint_auth_method=client_secret_post during dynamic
        // client registration, but Rezi's authorization server only accepts "none" (public
        // client, PKCE-only). Registered a client by hand via curl to work around this and
        // pinned the resulting client_id here, which skips the SDK's DCR path entirely.
        ClientId = "afa47473-5533-4643-a5f2-4ce1333f98c6",
        TokenCache = new FileTokenCache("rezi-token.json"),
    }
}, httpClient, consoleLoggerFactory);

var stopwatch = Stopwatch.StartNew();
var client = await McpClient.CreateAsync(
    transport,
    // Pinning an initialize-capable version skips the SDK's separate "server/discover" probe,
    // which otherwise triggers its own 401/OAuth challenge before "initialize" triggers a second
    // one — cutting the number of required browser sign-ins from two down to one.
    clientOptions: new() { InitializationTimeout = TimeSpan.FromMinutes(5), ProtocolVersion = "2025-03-26" },
    loggerFactory: consoleLoggerFactory);
Console.WriteLine($"Connected + authenticated in {stopwatch.Elapsed}.");
Console.WriteLine();

var tools = await client.ListToolsAsync();
Console.WriteLine($"Found {tools.Count} tools:");
foreach (var tool in tools)
{
    Console.WriteLine($"  - {tool.Name}: {tool.Description}");
}
Console.WriteLine();

foreach (var toolName in new[] { "read_resume", "write_resume", "get_resume_format" })
{
    var tool = tools.FirstOrDefault(t => t.Name == toolName);
    Console.WriteLine($"=== {toolName} inputSchema ===");
    Console.WriteLine(tool is null ? "(not found)" : tool.JsonSchema.ToString());
    Console.WriteLine();
}

Console.WriteLine("=== get_resume_format() ===");
var formatResult = await client.CallToolAsync("get_resume_format", new Dictionary<string, object?>());
foreach (var block in formatResult.Content)
{
    if (block is TextContentBlock text) Console.WriteLine(text.Text);
}
Console.WriteLine();

const string forEditId = "V22et70NfJ4jCcE1L9h0";

async Task<JsonNode> ReadAsync()
{
    var result = await client.CallToolAsync("read_resume", new Dictionary<string, object?> { ["resume_id"] = forEditId });
    var text = result.Content.OfType<TextContentBlock>().Single().Text;
    return JsonNode.Parse(text)!;
}

async Task<string> ReadSummaryAsync()
{
    var resume = await ReadAsync();
    return resume["data"]!["summary"]!["summary"]!.GetValue<string>();
}

var originalSummary = await ReadSummaryAsync();
Console.WriteLine($"=== original summary ===\n{originalSummary}\n");

const string marker = " [joblens-phase1-roundtrip-test]";
var markedSummary = originalSummary + marker;

Console.WriteLine("=== write_resume: appending round-trip marker ===");
var writePayload = new JsonObject
{
    ["data"] = new JsonObject { ["summary"] = new JsonObject { ["summary"] = markedSummary } },
};
await client.CallToolAsync("write_resume", new Dictionary<string, object?>
{
    ["resume_id"] = forEditId,
    ["resume"] = writePayload,
});

var afterWrite = await ReadSummaryAsync();
Console.WriteLine($"=== summary after write ===\n{afterWrite}\n");
Console.WriteLine(afterWrite == markedSummary ? "MARKER PRESENT - write round-tripped." : "MISMATCH - marker not found as expected!");

Console.WriteLine();
Console.WriteLine("=== write_resume: restoring original summary ===");
var restorePayload = new JsonObject
{
    ["data"] = new JsonObject { ["summary"] = new JsonObject { ["summary"] = originalSummary } },
};
await client.CallToolAsync("write_resume", new Dictionary<string, object?>
{
    ["resume_id"] = forEditId,
    ["resume"] = restorePayload,
});

var afterRestore = await ReadSummaryAsync();
Console.WriteLine($"=== summary after restore ===\n{afterRestore}\n");
Console.WriteLine(afterRestore == originalSummary ? "RESTORED - matches original exactly." : "MISMATCH - restore did not match original!");

/// Handles the OAuth authorization URL by starting a local HTTP server and opening a browser.
static async Task<AuthorizationResult?> HandleAuthorizationUrlAsync(
    AuthorizationCallbackContext authorizationContext,
    CancellationToken cancellationToken)
{
    var authorizationUrl = authorizationContext.AuthorizationUri;
    var redirectUri = authorizationContext.RedirectUri;

    Console.WriteLine("Starting OAuth authorization flow...");
    Console.WriteLine($"Opening browser to: {authorizationUrl}");

    var listenerPrefix = redirectUri.GetLeftPart(UriPartial.Authority);
    if (!listenerPrefix.EndsWith("/")) listenerPrefix += "/";

    using var listener = new HttpListener();
    listener.Prefixes.Add(listenerPrefix);

    try
    {
        listener.Start();
        Console.WriteLine($"Listening for OAuth callback on: {listenerPrefix}");

        OpenBrowser(authorizationUrl);

        var context = await listener.GetContextAsync();
        var query = HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
        var code = query["code"];
        var state = query["state"];
        var iss = query["iss"];
        var error = query["error"];

        string responseHtml = "<html><body><h1>Authentication complete</h1><p>You can close this window now.</p></body></html>";
        byte[] buffer = Encoding.UTF8.GetBytes(responseHtml);
        context.Response.ContentLength64 = buffer.Length;
        context.Response.ContentType = "text/html";
        context.Response.OutputStream.Write(buffer, 0, buffer.Length);
        context.Response.Close();

        if (!string.IsNullOrEmpty(error))
        {
            Console.WriteLine($"Auth error: {error}");
            return null;
        }

        if (string.IsNullOrEmpty(code))
        {
            Console.WriteLine("No authorization code received");
            return null;
        }

        Console.WriteLine("Authorization code received successfully.");
        return new AuthorizationResult { Code = code, State = state, Iss = iss };
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error getting auth code: {ex.Message}");
        return null;
    }
    finally
    {
        if (listener.IsListening) listener.Stop();
    }
}

static void OpenBrowser(Uri url)
{
    if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
    {
        Console.WriteLine("Error: Only HTTP and HTTPS URLs are allowed.");
        return;
    }

    try
    {
        var psi = new ProcessStartInfo
        {
            FileName = url.ToString(),
            UseShellExecute = true
        };
        Process.Start(psi);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error opening browser: {ex.Message}");
        Console.WriteLine($"Please manually open this URL: {url}");
    }
}

// Diagnostic only: persists the token in plaintext to prove cross-process reuse.
// A real implementation would encrypt this at rest (see Phase 1).
sealed class FileTokenCache(string path) : ITokenCache
{
    public async ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine("=== Token issued, persisting to disk ===");
        Console.WriteLine($"RefreshToken present: {tokens.RefreshToken is not null}");
        Console.WriteLine($"ExpiresIn: {tokens.ExpiresIn?.ToString() ?? "(none)"} seconds");
        Console.WriteLine("==========================================");
        Console.WriteLine();

        var json = JsonSerializer.Serialize(tokens);
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }

    public async ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine("[FileTokenCache] No cached token on disk.");
            return null;
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken);
        var tokens = JsonSerializer.Deserialize<TokenContainer>(json);
        Console.WriteLine($"[FileTokenCache] Loaded cached token from disk (obtained {tokens?.ObtainedAt}, expires in {tokens?.ExpiresIn}s).");
        return tokens;
    }
}
