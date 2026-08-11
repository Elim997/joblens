namespace JobLens.Core.Resume;

/// <summary>
/// A Rezi MCP tool call completed (not a transport/auth failure) but reported IsError=true -
/// a genuine failure from Rezi's side (e.g. an invalid resume id), distinct from
/// ReziAuthenticationRequiredException so callers (see /tailor) can map each to a sensible
/// HTTP status instead of both surfacing as an opaque 500.
/// </summary>
public sealed class ReziToolCallException(string toolName, string reziError)
    : InvalidOperationException($"Rezi tool '{toolName}' failed: {reziError}")
{
    public string ToolName { get; } = toolName;
    public string ReziError { get; } = reziError;
}
