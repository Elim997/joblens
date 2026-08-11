namespace JobLens.Core.Resume;

/// <summary>
/// Thrown instead of letting an interactive OAuth flow start mid-request. RealResumeClient
/// never opens a browser itself - Rezi's access tokens last ~30 days with no refresh token, so
/// re-authentication is a deliberate, occasional step (see tools/ReziLogin), not something that
/// should silently block or hang an API request.
/// </summary>
public sealed class ReziAuthenticationRequiredException()
    : InvalidOperationException(
        "No valid Rezi session (token missing or expired). Run the re-login step: " +
        "`dotnet run --project tools/ReziLogin` from the repo root. It opens a one-time " +
        "browser sign-in and saves a new ~30-day token; JobLens picks it up automatically " +
        "on the next request, no restart needed. See SETUP.md \"Rezi resume tailoring\".");
