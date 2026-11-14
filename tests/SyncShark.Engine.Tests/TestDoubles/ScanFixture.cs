using System.Runtime.CompilerServices;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests;

/// <summary>Owns the current token for the existing single-scan fixtures; product calls never infer a token.</summary>
internal static class ScanFixture
{
    private static readonly ConditionalWeakTable<IStateStore, Dictionary<string, Guid>> Tokens = new();

    private static IStateStore Owner(IStateStore store) => store switch
    {
        ForwardingStateStore forwarding => Owner(forwarding.FixtureInner),
        StoreCounters counted => Owner(counted.FixtureInner),
        _ => store
    };

    internal static Guid TokenOrNone(IStateStore store, string pairId) =>
        Tokens.TryGetValue(Owner(store), out var tokens) ? tokens.GetValueOrDefault(pairId) : Guid.Empty;

    internal static Guid Token(IStateStore store, string pairId) => Tokens.GetOrCreateValue(Owner(store))[pairId];

    internal static Guid BeginScan(this IStateStore store, string pairId, long generation, bool includeDeleted, string? scope = null)
    {
        var tokens = Tokens.GetOrCreateValue(Owner(store));
        if (tokens.TryGetValue(pairId, out var previous))
            store.EndScanAsync(pairId, previous, 1).GetAwaiter().GetResult();
        var token = Guid.NewGuid();
        store.BeginScan(pairId, token, generation, includeDeleted, scope);
        tokens[pairId] = token;
        return token;
    }

    internal static IScanSink OpenScan(this IStateStore store, string pairId, Side side, int batchRows) => store.OpenScan(pairId, Token(store, pairId), side, batchRows);
    internal static IEnumerable<Row> ScanRows(this IStateStore store, string pairId, Func<Row, RowContext> context) => store.ScanRows(pairId, Token(store, pairId), context);
    internal static IReadOnlyList<RenameCandidate> ScanRenames(this IStateStore store, string pairId) => store.ScanRenames(pairId, Token(store, pairId));
    internal static Entry? ScanEntry(this IStateStore store, string pairId, Side side, string key) => store.ScanEntry(pairId, Token(store, pairId), side, key);
    internal static bool InScan(this IStateStore store, string pairId, Side side, string key) => store.InScan(pairId, Token(store, pairId), side, key);
    internal static bool HasDeletedNames(this IStateStore store, string pairId) => store.HasDeletedNames(pairId, Token(store, pairId));
    internal static bool DeletedNameExact(this IStateStore store, string pairId, string key) => store.DeletedNameExact(pairId, Token(store, pairId), key);
}
