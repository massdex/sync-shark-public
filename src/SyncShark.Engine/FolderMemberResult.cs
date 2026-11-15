namespace Massdex.SyncShark.Engine;

/// <summary>
/// One submission member's receipt certainty. Ordinal is the original wire position,
/// resolved through the immutable submission link, never a path or operation item ordinal.
/// </summary>
public abstract record FolderMemberResult(int Ordinal)
{
    /// <summary>A provider-confirmed deletion receipt, not permission to settle a mapping or complete a root.</summary>
    public sealed record Receipt(int Ordinal, Entry Entry) : FolderMemberResult(Ordinal);

    /// <summary>A positively established no-effect outcome; a later replan needs a new operation.</summary>
    public sealed record KnownNoEffect(int Ordinal, TextKey? Cause) : FolderMemberResult(Ordinal);

    /// <summary>Missing, duplicate, malformed or uncertain evidence grants neither success nor replay.</summary>
    public sealed record Unknown(int Ordinal, TextKey? Cause) : FolderMemberResult(Ordinal);
}
