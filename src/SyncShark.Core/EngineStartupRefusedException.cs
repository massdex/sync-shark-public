using Massdex.SyncShark.V2;

namespace Massdex.SyncShark;

/// <summary>A metadata-only refusal; the location is diagnostic, never cleanup authority.</summary>
public sealed class EngineStartupRefusedException(string reasonKey, string location, Exception? inner = null)
    : SyncCliException(reasonKey, EngineStartupAdmission.RefusalMessage,
        SyncCliExitCode.NeedsReview, inner)
{
    public string ReasonKey { get; } = reasonKey;
    public string Location { get; } = location;
}
