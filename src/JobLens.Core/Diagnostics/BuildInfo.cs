using System.Reflection;

namespace JobLens.Core.Diagnostics;

/// <summary>
/// Exposes the running process's build identity - the entry assembly's
/// AssemblyInformationalVersion, which the .NET SDK automatically suffixes with
/// "+&lt;SourceRevisionId&gt;" once that MSBuild property is set (see JobLens.Api.csproj's
/// SetSourceRevisionIdFromGit target; IncludeSourceRevisionInInformationalVersion defaults to
/// true, no SourceLink package required). A stale published binary (see README's Task Scheduler
/// design) is detectable from this string alone - logged at startup and returned by GET /health -
/// without comparing behavior to source.
///
/// Reads Assembly.GetEntryAssembly() rather than GetExecutingAssembly() so the marker reflects
/// whichever assembly was actually launched (JobLens.Api.exe in production; a test host under
/// xunit) regardless of which project this helper type physically lives in - only JobLens.Api has
/// the SourceRevisionId target today, so its own version is what actually carries a commit SHA
/// suffix.
/// </summary>
public static class BuildInfo
{
    public static string Marker { get; } = ResolveMarker();

    private static string ResolveMarker()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return string.IsNullOrWhiteSpace(informationalVersion) ? "unknown" : informationalVersion;
    }
}
