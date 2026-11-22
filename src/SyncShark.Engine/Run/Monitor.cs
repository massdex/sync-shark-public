namespace Massdex.SyncShark.Engine.Run;

/// <summary>Distinguishes a verified source reset from a failed read or ordinary progress.</summary>
public static class Monitor
{
    public static async Task ObserveAsync(PairConfig pair, ISide side, IStore store, CancellationToken ct)
    {
        var volume = pair.Roots?.LocalVolumeKey();
        var cursor = side.Side == Side.Dropbox ? await store.CursorAsync(pair.PairId,ct) : null;
        var position = side.Side == Side.Local && volume != null ? await store.PositionAsync(volume,ct) : null;
        var page = await side.ReadChangesAsync(cursor,position,ct);
        await ObserveAsync(pair,side.Side,page,store,ct);
    }

    internal static async Task ObserveAsync(PairConfig pair, Side side, ChangePage page, IStore store, CancellationToken ct)
    {
        if (!page.ContinuityLost) return;
        if (side == Side.Dropbox && !string.IsNullOrEmpty(page.Cursor))
            await store.ResetCursorAsync(pair.PairId,page.Cursor,nameof(ChangePage.ContinuityLost),ct);
        else if (side == Side.Local && page.Position is {} fresh && pair.Roots?.LocalVolumeKey() is {} volume)
        {
            var members = new List<string>();
            await foreach (var member in store.PairsAsync(ct))
                if (member.Roots?.LocalVolumeKey() == volume) members.Add(member.PairId);
            await store.ResetJournalAsync(volume,fresh,members,nameof(ChangePage.ContinuityLost),ct);
        }
        else throw new InventoryIncompleteException(nameof(ChangePage.ContinuityLost),null);
    }
}
