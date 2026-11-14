namespace Massdex.SyncShark.Engine;

/// <summary>One ordered change notice from a source listener, with the acquisition evidence needed before
/// execution. Path is the observed path and OldPath the previous path of a rename. Id is the native identity
/// when the source supplied one. Observed is the provider entry read with the notice: feed evidence. It never
/// replaces a decisive freshness check before an effect. The only reads it can spare are a Dropbox echo's,
/// under trusted sources (engine.md §8.7, Echoes): an entry that equals its saved binding, or a tombstone with
/// no saved binding at or under its path, whose settlement does nothing.
/// LocalContentDirty survives coalescing, continuations and
/// failures. IdentityDeleted is full-reference Local FILE_DELETE evidence, not a path tombstone; a later
/// occupant of the same path is never covered by it, and Dropbox absence is verified at execution rather
/// than inferred from a deletion page. Usn is the Local journal record that produced the notice, or null for
/// any other source; only the dirty owner compares it.</summary>
public sealed record ChangeNotice(
    Side Side,
    ChangeKind Kind,
    string Path,
    string? OldPath,
    string? Id,
    Entry? Observed,
    bool LocalContentDirty,
    bool IdentityDeleted,
    ulong? Usn = null);
