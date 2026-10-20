using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Massdex.SyncShark.V2;

/// <summary>
/// The live items a folder Delete removes. The Planner keeps one Delete per folder ; the Executor
/// expands it over everything live under the folder, the first-sync review guard lists the same victims without a
/// second copy of the rule, and the run's execution-time re-check reuses it.
/// </summary>
internal static class FolderDelete
{
    /// <summary>
    /// a Dropbox folder Delete over at least this many live descendants is deleted as one whole
    /// folder; a smaller one keeps the per-item path.
    /// </summary>
    internal const int WholeFolderMinimum = 50;

    /// <summary>True when the executor expands the Delete over the folder's live contents instead of deleting it alone.</summary>
    internal static bool Expands(SyncAction action) =>
        action.Kind == ActionKind.Delete && action.ExpectedTarget?.Kind == ItemKind.Folder &&
        !action.DescendantsPlanned;

    /// <summary>The live items strictly under the folder on its side, as the executor expands the Delete over them.</summary>
    internal static IReadOnlyList<ItemState> LiveDescendants(IStateStore store, string rootId, string folderPath) =>
        store.LiveUnder(rootId, [folderPath]).Where(item => !PairPath.Same(item.Path, folderPath)).ToArray();

    /// <summary>Children with standalone sync semantics; ignored children travel with the parent effect.</summary>
    internal static IReadOnlyList<ItemState> SyncedDescendants(IStateStore store, PairDef pair, string rootId,
        string folderPath, ProviderRefusedPaths? refused = null)
    {
        var rules = PairInclude.IgnoreRules(pair);
        refused ??= ProviderRefusedPaths.Load(store, pair, rootId);
        return LiveDescendants(store, rootId, folderPath).Where(item =>
            !refused.Contains(item) &&
            !SyncIgnorePolicy.IsIgnored(item.Path, item.Kind == ItemKind.Folder, rules)).ToArray();
    }

    /// <summary>The stable identity of one deleted item: its side ("A" or "B") and path, matched ignoring case.</summary>
    internal static string VictimKey(Side side, string path) => (side == Side.A ? "A" : "B") + ":" + path;

    /// <summary>
    /// Every item the plan would delete on either side, each collapsed folder Delete expanded over its live
    /// descendants exactly as the executor expands it. The first-sync review guard records this at prepare and
    /// recomputes the guarded subset at apply (both in the CLI and again at execution time) to refuse any first-sync
    /// decision the user did not see .
    /// </summary>
    internal static IReadOnlyList<string> VictimKeys(IStateStore store, PairDef pair, Plan plan)
    {
        var refused = ProviderRefusedPaths.Load(store, pair);
        var victims = new List<string>();
        foreach (var action in plan.Actions)
            if (action.Kind == ActionKind.Delete)
                victims.AddRange(ActionVictims(store, pair, action, action.Target == Side.A ? refused.A : refused.B));
        return victims;
    }

    /// <summary>
    /// Splits the plan's delete victims for the first-sync guard . <c>Guarded</c> are the victims
    /// the reviewed prepare must show: every delete carrying <see cref="SyncAction.FirstSyncChoice"/> (one-sided
    /// remove, per-path winner, rebuildable, and a collapsed folder that kept the flag), plus a rule-19
    /// deletion-evidence delete (<see cref="SyncAction.NeedsReview"/>) whose path was neither in the reviewed set nor
    /// baselined. <c>Propagated</c> are the ordinary post-prepare deletions — baseline rule 6, reviewed evidence
    /// deletes and collision/move deletes — that are ongoing sync (RQ-043). Both are deduplicated ignoring case.
    /// </summary>
    internal static (IReadOnlyList<string> Guarded, IReadOnlyList<string> Propagated) SplitVictims(
        IStateStore store, PairDef pair, Plan plan, IReadOnlySet<string> reviewed)
    {
        var refused = ProviderRefusedPaths.Load(store, pair);
        var guarded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var propagated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string>? baselined = null;
        foreach (var action in plan.Actions)
        {
            if (action.Kind != ActionKind.Delete) continue;
            foreach (var victim in ActionVictims(store, pair, action, action.Target == Side.A ? refused.A : refused.B))
            {
                if (reviewed.Contains(victim)) continue;
                if (action.FirstSyncChoice)
                    guarded.Add(victim);
                else if (action.NeedsReview &&
                         !(baselined ??= BaselineVictimKeys(store, pair)).Contains(victim))
                    guarded.Add(victim);
                else
                    propagated.Add(victim);
            }
        }
        propagated.ExceptWith(guarded);
        return (Ordered(guarded), Ordered(propagated));
    }

    /// <summary>True when the delete exists because of a first-sync choice the prepare reviewed (decision 71).</summary>
    internal static bool IsFirstSyncChoice(SyncAction action) => action.FirstSyncChoice;

    /// <summary>
    /// finding 4: the deterministic signature of a folder Delete's live descendants. A plan freezes
    /// it; the Executor expands the folder only while the live set still has exactly this signature. The signature
    /// covers each descendant's path, object, version, hash, kind, size, modification time and flags.
    /// </summary>
    internal static string Signature(IReadOnlyList<ItemState> descendants)
    {
        var text = new StringBuilder();
        foreach (var item in descendants.OrderBy(item => item.Path, StringComparer.Ordinal))
            text.Append(item.Path).Append('\0')
                .Append(item.ObjectId).Append('\0')
                .Append(item.VersionId ?? string.Empty).Append('\0')
                .Append(item.Hash ?? string.Empty).Append('\0')
                .Append(item.Kind == ItemKind.Folder ? 'd' : 'f').Append('\0')
                .Append(item.Size.ToString(CultureInfo.InvariantCulture)).Append('\0')
                .Append(item.Mtime.UtcTicks.ToString(CultureInfo.InvariantCulture)).Append('\0')
                .Append((int)item.Flags).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static IEnumerable<string> ActionVictims(IStateStore store, PairDef pair, SyncAction action, ProviderRefusedPaths refused)
    {
        yield return VictimKey(action.Target, action.Path);
        if (!Expands(action)) yield break;
        var rootId = action.Target == Side.A ? pair.A.RootId : pair.B.RootId;
        foreach (var item in SyncedDescendants(store, pair, rootId, action.Path, refused))
            yield return VictimKey(action.Target, item.Path);
    }

    private static HashSet<string> BaselineVictimKeys(IStateStore store, PairDef pair)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var baseline in store.Baselines(pair.PairId))
        {
            keys.Add(VictimKey(Side.A, baseline.A.Path));
            keys.Add(VictimKey(Side.B, baseline.B.Path));
        }
        return keys;
    }

    private static string[] Ordered(HashSet<string> victims) =>
        victims.OrderBy(victim => victim, StringComparer.Ordinal).ToArray();
}
