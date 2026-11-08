using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.Engine;

/// <summary>One item to decide. Extra = the further entries that share this key (D23 ambiguity); empty when
/// unambiguous. LocalHash = the local file's content hash once the Executor has computed it; null until requested.
/// Deletion = the prepared DeletionEvidence for this row's target; null until the Executor asks for it and null again
/// after any re-observation that invalidates it (P4-06). Null is "no answer yet", never a deletion answer.</summary>
public sealed record Row(string Key, Entry? Local, Entry? Dropbox, SyncedEntry? Synced,
    IReadOnlyList<Entry> Extra, string? LocalHash, RowContext Context, DeletionEvidence? Deletion = null)
{
    /// <summary>Builds the context from this row's own facts without allocating a placeholder copy.</summary>
    public Row(Func<Row, RowContext> context, string key, Entry? local, Entry? dropbox, SyncedEntry? synced,
        IReadOnlyList<Entry> extra, string? localHash)
        : this(key, local, dropbox, synced, extra, localHash, null!) => Context = context(this);

    // An absent observation still has an exact path; its comparison key cannot recover the spelling.
    internal string? ObservationPath { get; init; }

    /// <summary>True when more than one entry shares this row's key.</summary>
    public bool Ambiguous => Extra.Count > 0;

    /// <summary>The observed entry on a side; the saved entry is not a fallback, and any non-local side selects Dropbox.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Entry? EntryOn(Side side) => side == Side.Local ? Local : Dropbox;

    /// <summary>The row's path on a side: the observed entry, else the target, else the saved path.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string? PathOn(Side side, string? target = null) =>
        EntryOn(side)?.Path ?? target ?? Synced?.PathOn(side);

    /// <summary>True when the observed or saved item is a folder; the local observation wins. Extra is not consulted.
    /// A method, not a property, so the record's ToString and JSON shape stay unchanged.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsFolder() => (Local ?? Dropbox)?.IsFolder ?? Synced?.IsFolder ?? false;

    /// <summary>The display/failure path, preferring current Local then Dropbox, then the saved Local path.</summary>
    public static string Path(Row row) => row.Local?.Path ?? row.Dropbox?.Path ?? row.Synced?.LocalPath ?? row.ObservationPath ?? row.Key;

    /// <summary>Both endpoint paths with the observed, target, saved and destination fallbacks.</summary>
    public static (string Local, string Dropbox) Paths(Row row, string? target = null) =>
        (row.PathOn(Side.Local, target) ?? row.Context.Destination ?? row.ObservationPath ?? row.Key,
         row.PathOn(Side.Dropbox, target) ?? row.Context.Destination ?? row.ObservationPath ?? row.Key);

    /// <summary>The comparison key followed by observed and saved side keys, retaining their order and duplicates.</summary>
    public static IEnumerable<string> Keys(Row row)
    {
        yield return row.Key;
        if (row.Local is { } local) yield return PathNames.KeyFor(Side.Local, local.Path);
        if (row.Dropbox is { } remote) yield return PathNames.KeyFor(Side.Dropbox, remote.Path);
        if (row.Synced is { } saved)
        {
            yield return saved.LocalKey;
            yield return saved.DropboxKey;
        }
    }

    /// <summary>One row for a single-side observation; an invalid side populates neither entry.</summary>
    public static Row ForOneSide(Side side, string key, Entry? entry, SyncedEntry? synced,
        IReadOnlyList<Entry> extra, string? localHash, RowContext context) =>
        new(key, side == Side.Local ? entry : null, side == Side.Dropbox ? entry : null, synced, extra, localHash,
            context);
}
