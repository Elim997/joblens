namespace JobLens.Core.Resume;

/// <summary>
/// The tailoring chat client could not be reached, or failed at the transport/model level
/// (auth, network, non-2xx, timeout). Distinct from TailoringOutputInvalidException, which means
/// the model responded but its output could not be parsed into a usable shape. The message is
/// built from the failing exception's type name only, never its .Message - no upstream response
/// body, prompt, or authorization header can leak into logs or an API error response this way.
/// </summary>
public sealed class TailoringModelUnavailableException(string message) : InvalidOperationException(message);
