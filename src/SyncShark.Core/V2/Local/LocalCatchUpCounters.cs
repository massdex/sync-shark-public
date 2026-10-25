using System.Diagnostics;

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

    internal long ObjectBatches { get; private set; }
    internal long NativeProbes { get; private set; }
    internal double NativeMilliseconds { get; private set; }
    internal long NameSteps { get; private set; }
    internal double NameMilliseconds { get; private set; }
    internal void ObjectBatch() => ObjectBatches++;
    internal void ObjectLookup() => ByObjectCalls++;
    internal void NameLookup() => HasMultipleNamesCalls++;
    internal void NativeProbe(TimeSpan elapsed) { NativeProbes++; NativeMilliseconds += elapsed.TotalMilliseconds; }

    internal IEnumerable<DirectoryEntryInfo> ListNames(string directory)
    {
        using var rows = WindowsDirectoryListing.List(directory).GetEnumerator();
        while (true)
        {
            var started = Stopwatch.GetTimestamp();
            bool next;
            try { next = rows.MoveNext(); }
            finally { NameSteps++; NameMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
            if (!next) yield break;
            yield return rows.Current;
        }
    }

    internal void Emit(string rootId, TimeSpan elapsed) =>
        RunTrace.Emit($"local journal catch-up ({rootId}): records={Records}; resolved={Resolved}; ambiguous={Ambiguous}; refreshed={Refreshed}; recursive={Recursive}; byObject={ByObjectCalls}; hasMultipleNames={HasMultipleNamesCalls}; object-batches={ObjectBatches}; link-probes={NativeProbes}; link-ms={NativeMilliseconds:F0}; name-steps={NameSteps}; name-ms={NameMilliseconds:F0}; elapsed-ms={elapsed.TotalMilliseconds:F0}");
}
