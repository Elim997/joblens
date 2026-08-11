// Interactive re-login for Rezi's MCP server. Run this whenever RealResumeClient throws
// ReziAuthenticationRequiredException: it opens a one-time browser sign-in and saves a fresh
// ~30-day token to the same encrypted cache RealResumeClient reads, so the running JobLens.Api
// service picks it up on its very next request with no restart required.
// See CLAUDE.md Phase 1 and SETUP.md "Rezi resume tailoring".

using System.Diagnostics;
using System.Net;
using System.Text;
using System.Web;
using JobLens.Core.Resume;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;

var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Warning));
var tokenCachePath = ReziMcpConnection.DefaultTokenCachePath;
var tokenCache = new EncryptedFileTokenCache(tokenCachePath, loggerFactory.CreateLogger<EncryptedFileTokenCache>());

Console.WriteLine("Rezi login");
Console.WriteLine($"Token cache: {tokenCachePath}");
Console.WriteLine();

using var httpClient = new HttpClient();
var transport = ReziMcpConnection.CreateTransport(httpClient, tokenCache, HandleAuthorizationUrlAsync, loggerFactory);

await using var client = await McpClient.CreateAsync(
    transport,
    clientOptions: new()
    {
        ProtocolVersion = ReziMcpConnection.ProtocolVersion,
        InitializationTimeout = TimeSpan.FromMinutes(5),
    });

Console.WriteLine("Signed in. Verifying with list_resumes...");
await using var resumeClient = new RealResumeClient(tokenCache, loggerFactory.CreateLogger<RealResumeClient>());
var resumes = await resumeClient.ListResumesAsync();
Console.WriteLine($"OK - {resumes.Count} resume(s) visible:");
foreach (var r in resumes)
    Console.WriteLine($"  - {r.Name} ({r.Id})");

static async Task<AuthorizationResult?> HandleAuthorizationUrlAsync(
    AuthorizationCallbackContext authorizationContext, CancellationToken cancellationToken)
{
    var authorizationUrl = authorizationContext.AuthorizationUri;
    var redirectUri = authorizationContext.RedirectUri;

    Console.WriteLine("Opening browser to sign in to Rezi...");
    Console.WriteLine($"If it doesn't open automatically, visit: {authorizationUrl}");

    var listenerPrefix = redirectUri.GetLeftPart(UriPartial.Authority);
    if (!listenerPrefix.EndsWith('/')) listenerPrefix += "/";

    using var listener = new HttpListener();
    listener.Prefixes.Add(listenerPrefix);
    listener.Start();

    OpenBrowser(authorizationUrl);

    var context = await listener.GetContextAsync();
    var query = HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
    var code = query["code"];
    var state = query["state"];
    var iss = query["iss"];
    var error = query["error"];

    const string responseHtml = "<html><body><h1>Signed in</h1><p>You can close this window and return to the terminal.</p></body></html>";
    var buffer = Encoding.UTF8.GetBytes(responseHtml);
    context.Response.ContentLength64 = buffer.Length;
    context.Response.ContentType = "text/html";
    context.Response.OutputStream.Write(buffer, 0, buffer.Length);
    context.Response.Close();
    listener.Stop();

    if (!string.IsNullOrEmpty(error))
    {
        Console.WriteLine($"Rezi returned an authorization error: {error}");
        return null;
    }

    if (string.IsNullOrEmpty(code))
    {
        Console.WriteLine("No authorization code received.");
        return null;
    }

    return new AuthorizationResult { Code = code, State = state, Iss = iss };
}

static void OpenBrowser(Uri url)
{
    if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
    {
        Console.WriteLine("Refusing to open a non-http(s) URL.");
        return;
    }

    try
    {
        Process.Start(new ProcessStartInfo { FileName = url.ToString(), UseShellExecute = true });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Could not open a browser automatically ({ex.Message}).");
        Console.WriteLine($"Please open this URL manually: {url}");
    }
}
