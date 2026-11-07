namespace Massdex.SyncShark.Engine;

/// <summary>One ordered change notice from a source listener, with the acquisition evidence needed before
/// execution. Path is the observed path and OldPath the previous path of a rename. Id is the native identity
/// when the source supplied one. Observed is the provider entry read with the notice: feed evidence, never
/// permission to skip decisive freshness checks. LocalContentDirty survives coalescing, continuations and
/// failures. IdentityDeleted is full-reference Local FILE_DELETE evidence, not a path tombstone; a later
/// occupant of the same path is never covered by it, and Dropbox absence is verified at execution rather
/// than inferred from a deletion page.</summary>
public sealed record ChangeNotice(
    Side Side,
    ChangeKind Kind,
    string Path,
    string? OldPath,
    string? Id,
    Entry? Observed,
    bool LocalContentDirty,
    bool IdentityDeleted);
