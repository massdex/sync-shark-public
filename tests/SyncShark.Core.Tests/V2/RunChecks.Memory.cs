using System.Collections;
using System.Reflection;

namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    // Called by the main check runner; no timing/GC thresholds or large synthetic inventories.
    public static async Task MemoryReductionChecksAsync()
    {
        Scope_ReusesUnchangedRows();
        Scope_WithoutHeldReusesInput();
        await Preview_ApplyUsesLookupWithoutRecapturingMatchesAsync();
        Console.WriteLine("PASS MemoryReductionChecks");
    }

    private static void Scope_ReusesUnchangedRows()
    {
        using var store = new InMemoryStateStore();
        var item = new ItemState("kept.txt", ItemKind.File, "id", null, null, 1,
            DateTimeOffset.UnixEpoch, ItemFlags.None);
        IReadOnlyList<ItemState> items = new[] { item };
        var hide = typeof(RunScope).GetMethod("Hide", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var scope = new RunScope(store, _ => true, _ => true);
        Require(ReferenceEquals(items, hide.Invoke(scope, [items, Side.A])),
            "unchanged scope returns its input collection");

        var ignored = item with { Flags = ItemFlags.Ignored };
        IReadOnlyList<ItemState> ignoredItems = new[] { ignored };
        var excluding = new RunScope(store, _ => false, _ => false);
        Require(ReferenceEquals(ignoredItems, hide.Invoke(excluding, [ignoredItems, Side.A])),
            "already-ignored rows need no flags clone or collection");
        var changed = (IReadOnlyList<ItemState>)hide.Invoke(excluding, [items, Side.A])!;
        Require(changed.Count == 1 && changed[0].Flags == ItemFlags.Ignored && item.Flags == ItemFlags.None,
            "new ignored flags clone the row without mutating input or adding carried-ignore");
    }

    private static void Scope_WithoutHeldReusesInput()
    {
        var scope = new RunScope.Scope([], [], []);
        var method = typeof(SyncRun).GetMethod("WithoutHeld", BindingFlags.Static | BindingFlags.NonPublic)!;
        Require(ReferenceEquals(scope, method.Invoke(null, [scope, Array.Empty<HeldDeletion>()])),
            "zero held paths retain the original scope");
    }

    private static async Task Preview_ApplyUsesLookupWithoutRecapturingMatchesAsync()
    {
        var fx = Build();
        fx.A.Seed("shared.txt", "same");
        fx.B.Seed("shared.txt", "same");
        fx.A.Seed("local-only.txt", "local");
        var run = fx.Run();
        var prepared = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(prepared.MatchedBothSides.Count == 1, "prepare still captures matched rows");
        var lookup = new LookupOnlyPrepareMatches();
        var (applied, rebuildables) = await run.PreviewFirstSyncApplyResolvedAsync(fx.Pair.PairId,
            null, null, [], lookup, new RecordingProgress(), CancellationToken.None);
        Require(applied.MatchedBothSides.Count == 0, "apply preview does not recapture matched rows");
        Require(ReferenceEquals(applied.FirstSyncOptions?.MatchedAtPrepare, lookup),
            "deferred replans retain the original prepared lookup");
        Require(lookup.ContainsCalls > 0, "deletion-probe exclusion queries ContainsKey without enumeration");
        Require(rebuildables.Cloud == Side.B && rebuildables.Selected.Count == 0,
            "empty frozen rebuildables retain the cloud side");
        Require(Decisions(prepared) == Decisions(applied), "memory changes preserve the preview's actions");
    }

    private sealed class LookupOnlyPrepareMatches : IReadOnlyDictionary<string, PrepareMatch>
    {
        public int ContainsCalls { get; private set; }
        public int Count => 0;
        public IEnumerable<string> Keys => throw new InvalidOperationException("Do not enumerate prepared keys");
        public IEnumerable<PrepareMatch> Values => throw new InvalidOperationException("Do not enumerate prepared values");
        public PrepareMatch this[string key] => throw new KeyNotFoundException();
        public bool ContainsKey(string key) { ContainsCalls++; return false; }
        public bool TryGetValue(string key, out PrepareMatch value) { value = null!; return false; }
        public IEnumerator<KeyValuePair<string, PrepareMatch>> GetEnumerator() =>
            throw new InvalidOperationException("Do not enumerate prepared matches");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
