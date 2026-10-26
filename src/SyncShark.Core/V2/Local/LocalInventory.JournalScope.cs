namespace Massdex.SyncShark.V2;

public sealed partial class LocalInventory
{
    // One window's uncertainty. It never escapes the window or authorizes absence.
    private sealed class JournalScope(LocalInventory owner, LocalJournalNames names, string rootId, CancellationToken ct)
    {
        private readonly HashSet<(ulong Object, ulong Parent)> _parents = [];
        private readonly Dictionary<string, ItemState> _ambiguous = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _scoped = new(StringComparer.OrdinalIgnoreCase);
        private long _characters;
        internal bool Overflow { get; private set; }

        private void Scope(string path, string reason)
        {
            if (Overflow) return;
            if (!_scoped.ContainsKey(path)) _characters += path.Length;
            _scoped[path] = reason;
            Overflow = _scoped.Count > Math.Min(owner._settings.LocalDeltaMaxPaths, PathProtectionIndex.DefaultPathLimit) ||
                _characters > PathProtectionIndex.DefaultCharacterLimit;
        }

        internal void Defer(NtfsUsnChange record, ItemState? known, string? scope, string reason)
        {
            Overflow |= names.ScopeOverflow;
            if (Overflow) return;
            var resolveNames = record.Name.Length == 0 && (record.Reason & 0x13100) != 0;
            if (scope is not null && !(resolveNames && known is null)) Scope(scope, reason);
            if (resolveNames && _parents.Add((record.FileId, record.ParentFileId)))
            {
                var parent = record.ParentFileId == owner._rootFileId ? "" :
                    owner.CatchUpByObject(rootId, SyncLocalEndpointStore.ObjectId(owner._volume, record.ParentFileId)) is { Kind: ItemKind.Folder } folder
                        ? folder.Path : null;
                if (parent is not null)
                {
                    var aliases = names.FindAmbiguous(record, parent, ct);
                    if (aliases is null) Scope(parent, "ambiguous journal parent could not be read; scoped retry required");
                    else foreach (var alias in aliases) Scope(alias, reason);
                    Overflow |= names.ScopeOverflow;
                }
            }
            if (Overflow || known is null || !_ambiguous.TryAdd(known.ObjectId, known)) return;
            for (var offset = 0; ; offset += 128)
            {
                var aliases = owner._known.ObjectPaths(rootId, known.ObjectId, offset, 128);
                foreach (var alias in aliases) Scope(alias, reason);
                if (aliases.Count < 128 || Overflow) break;
            }
            Scope(known.Path, reason);
        }

        internal void Hold() => owner._known.RequireRehashBatch(rootId,
            _ambiguous.Values.Where(item => item.Kind == ItemKind.File && !item.Flags.HasFlag(ItemFlags.OwnedPointer)),
            "ambiguous journal identity; scoped verification required");

        internal void Observe(Delta delta)
        {
            foreach (var change in delta.Changes)
            {
                if (Overflow) break;
                if (change.State is not { Kind: ItemKind.File } state) continue;
                ct.ThrowIfCancellationRequested();
                if (!names.Ambiguous(LocalItems.ParseObjectId(state.ObjectId).FileId, state, requirePresent: true)) continue;
                RunTrace.Emit("local journal post-refresh retry: " + names.LastFailure);
                Defer(new(LocalItems.ParseObjectId(state.ObjectId).FileId, 0, 0, 0, FileAttributes.Normal, ""),
                    owner.CatchUpByObject(rootId, state.ObjectId), state.Path,
                    names.LastFailure ?? "refreshed file has ambiguous hard-link identity");
            }
            CatchUp?.ObserveAmbiguous(_ambiguous.Count);
        }

        internal Delta? Apply(Delta delta)
        {
            if (Overflow) return null;
            if (_scoped.Count == 0) return delta;
            Hold();
            var index = PathProtectionIndex.TryCreate(_scoped.Keys);
            if (index is null) return null;
            bool Protected(string path) => _scoped.ContainsKey("") || index.Touches(path, false) || index.Touches(path, true);
            var retained = new List<Change>();
            foreach (var change in delta.Changes)
                if (Protected(change.Path) || _ambiguous.ContainsKey(change.ObjectId))
                    Scope(change.Path, _scoped.GetValueOrDefault(change.Path) ?? "change overlaps ambiguous journal identity; reread required");
                else retained.Add(change);
            if (Overflow) return null;
            RunTrace.Emit($"local journal scoped retry ({rootId}): {_scoped.Count} ambiguous path(s); remaining replay retained");
            return delta with
            {
                Changes = retained.ToArray(),
                IncompleteSubtrees = delta.IncompleteSubtrees.Concat(_scoped.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                IncompleteReasons = delta.IncompleteReasons.Concat(_scoped).GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase)
            };
        }
    }
}
