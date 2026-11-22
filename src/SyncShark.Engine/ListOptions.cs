namespace Massdex.SyncShark.Engine;

/// <summary>Arguments of ISide.ReadAllAsync.</summary>
public sealed record ListOptions(bool Recursive, bool IncludeOutOfScope,
    bool IncludeDeleted = false)
{
    /// <summary>Called with a directory's pair-relative path and the id read from its own enumeration handle, after
    /// that enumeration finished and the handle closed without error. Never called for the pair root or for a
    /// directory not opened. Null means nobody is asking.</summary>
    public Action<string, string>? DirectoryAcquired { get; init; }

    /// <summary>Requires each walked subfolder's own enumeration handle to carry the identity its parent
    /// listed, before any of its entries are yielded; a mismatch throws ChangedUnderneathException.</summary>
    public bool RequireListedIdentity { get; init; }

    /// <summary>Reports the current relative path and completed folder/page count at each listing boundary.</summary>
    public Action<string, long>? Progress { get; init; }

    /// <summary>Reports provider phase boundaries to the owning run without per-entry log traffic.</summary>
    public Action<RunEvent>? Events { get; init; }

    /// <summary>The inventory's own cursor for a finite continuation, never a source-feed cursor;
    /// entries are yielded in provider order with their displayed spelling.</summary>
    public string? Cursor { get; init; }

    /// <summary>Receives the terminal cursor only after all deferred spelling entries have been yielded.</summary>
    public Action<string>? CursorAcquired { get; init; }
}
