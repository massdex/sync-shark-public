using Draft = System.Collections.Generic.List<(Massdex.SyncShark.V2.PlanRow Row, Massdex.SyncShark.V2.SyncAction Action)>;

namespace Massdex.SyncShark.V2;

/// <summary>Planner draft index: keeps keep-both labels and post-pass C linear in the number of actions.</summary>
public static partial class Planner
{
    /// <summary>
    /// Draft positions by row, by (side, case-insensitive path), by folder move and by the folder move that rebased
    /// them. Owner: the Planner; readers: <see cref="FreeLabel"/> and post-pass C. Every add and path change registers
    /// here; lookups re-check the current draft entry, so a position whose entry changed since it was registered is
    /// skipped. Rows and action ids never change at a position, and no entry becomes a folder move after the build.
    /// Positions are kept in one node list chained per key (no list per key: most keys have one position).
    /// </summary>
    private sealed class DraftIndex
    {
        private readonly List<(int Position, int Next)> nodes = new();
        private readonly Dictionary<string, int>[] byPath =
        {
            new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase),
        };
        private readonly Dictionary<PlanRow, int> byRow = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, int> rebasedByMove = new(StringComparer.Ordinal);
        private readonly List<int> folderMoves = new();

        /// <summary>An index over every entry of the draft and post-pass A's rebase record.</summary>
        public static DraftIndex Over(Draft draft, IReadOnlyDictionary<string, HashSet<string>> rebasedBy)
        {
            var index = new DraftIndex();
            for (var i = 0; i < draft.Count; i++)
            {
                var action = draft[i].Action;
                index.Added(i, draft[i].Row, action);
                if (IsFolderMove(action))
                    index.folderMoves.Add(i);
                if (rebasedBy.TryGetValue(action.ActionId, out var moves))
                    foreach (var move in moves)
                        index.Push(index.rebasedByMove, move, i);
            }
            return index;
        }

        /// <summary>Registers a new entry at <paramref name="i"/>.</summary>
        public void Added(int i, PlanRow row, SyncAction action)
        {
            Repathed(i, action);
            Push(byRow, row, i);
        }

        /// <summary>Registers the entry at <paramref name="i"/> under its (new) target path.</summary>
        public void Repathed(int i, SyncAction action) => Push(byPath[(int)action.Target], action.Path, i);

        /// <summary>The positions registered at the path on the side (candidates; callers check the entry).</summary>
        public IEnumerable<int> At(Side side, string path) => Chain(byPath[(int)side], path);

        /// <summary>True when the draft creates something at the path on the side (dropped entries included).</summary>
        public bool AnyCreateAt(Draft draft, Side side, string path) =>
            At(side, path).Any(j => ActionPhases.IsCreate(draft[j].Action.Kind) && Addresses(draft[j].Action, side, path));

        /// <summary>The first draft position of a Delete at the path on the side from a row other than <paramref name="notRow"/>.</summary>
        public int FirstDeleteAt(Draft draft, Side side, string path, PlanRow notRow)
        {
            var first = -1;
            foreach (var j in At(side, path))
                if ((first < 0 || j < first) && draft[j].Action.Kind == ActionKind.Delete &&
                    Addresses(draft[j].Action, side, path) && !ReferenceEquals(draft[j].Row, notRow))
                    first = j;
            return first;
        }

        /// <summary>The draft positions of the row's actions.</summary>
        public IEnumerable<int> OfRow(PlanRow row) => Chain(byRow, row);

        /// <summary>The draft positions of the actions post-pass A rebased by the folder move with this action id.</summary>
        public IEnumerable<int> RebasedBy(string moveId) => Chain(rebasedByMove, moveId);

        /// <summary>The draft positions that held a folder move at the build, in draft order.</summary>
        public IReadOnlyList<int> FolderMoves => folderMoves;

        private static bool Addresses(SyncAction action, Side side, string path) =>
            action.Target == side && PairPath.Same(action.Path, path);

        private void Push<TKey>(Dictionary<TKey, int> heads, TKey key, int position) where TKey : notnull
        {
            nodes.Add((position, heads.TryGetValue(key, out var head) ? head : -1));
            heads[key] = nodes.Count - 1;
        }

        private IEnumerable<int> Chain<TKey>(Dictionary<TKey, int> heads, TKey key) where TKey : notnull
        {
            for (var n = heads.TryGetValue(key, out var head) ? head : -1; n >= 0; n = nodes[n].Next)
                yield return nodes[n].Position;
        }
    }
}
