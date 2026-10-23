namespace Massdex.SyncShark.V2;

/// <summary>Per-catch-up counters for ONE bounded journal-replay summary trace line; never per-item logging.</summary>
internal sealed class LocalCatchUpCounters(IStateStore store)
{
    // Per async flow, exactly like RunTrace.Slot: two roots catching up at once never mix figures.
    internal static readonly AsyncLocal<LocalCatchUpCounters?> Slot = new();
    internal static LocalCatchUpCounters? Current => Slot.Value;

    internal long Records { get; private set; }
    internal long Resolved { get; private set; }
    internal long Ambiguous { get; private set; }
    internal long Refreshed { get; private set; }
    internal long Recursive { get; private set; }
    internal long ByObjectCalls { get; private set; }
    internal long HasMultipleNamesCalls { get; private set; }

    internal void ObserveRecords(int count) => Records = count;
    internal void ObserveResolved(int count) => Resolved = count;
    internal void ObserveAmbiguous(int count) => Ambiguous = count;
    internal void ObserveRefresh(int items, int inspected)
    {
        Refreshed = items;
        Recursive = inspected;
    }

    internal ItemState? ByObject(string rootId, string objectId)
    {
        ByObjectCalls++;
        return store.ByObject(rootId, objectId);
    }

    internal void NameLookup() => HasMultipleNamesCalls++;

    internal void Emit(string rootId, TimeSpan elapsed) =>
        RunTrace.Emit($"local journal catch-up ({rootId}): records={Records}; resolved={Resolved}; ambiguous={Ambiguous}; refreshed={Refreshed}; recursive={Recursive}; byObject={ByObjectCalls}; hasMultipleNames={HasMultipleNamesCalls}; elapsed-ms={elapsed.TotalMilliseconds:F0}");
}
