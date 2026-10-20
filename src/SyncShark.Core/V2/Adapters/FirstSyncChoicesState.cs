using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Massdex.SyncShark.V2;

/// <summary>
/// The pair's retained first-sync prepare the digest of the reviewed choices, what each one-sided
/// choice was reported to do, the (side, path) of every item the reviewed plan would delete, and the paths the prepare
/// saw on both sides . SQLite retains large collections behind indexed, generation-bound views;
/// old per-pair JSON is imported incrementally and atomically. Non-SQLite fixtures retain the legacy JSON format.
/// A missing or unreadable record means there is no prepare to apply.
/// </summary>
internal static class FirstSyncChoicesState
{
    private const string DirectoryName = "v2";
    private const string FileNamePrefix = "first-sync-";
    private const string FileNameSuffix = ".choices.json";
    private const string UnreviewedDeletes =
        " not shown in the last prepare — run prepare again. For example: ";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // A freshly written record can be held for a moment by an antivirus scan or backup tool. Retry the transient
    // shapes briefly before treating the record as unreadable: one initial open plus two retries, 200 ms then
    // 500 ms. Corruption and every other error are not retried.
    private static readonly TimeSpan[] ReadRetryDelays =
        [TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(500)];

    /// <summary>Test seam: substitutes the record open so a briefly locked record needs no filesystem timing.</summary>
    internal static Func<string, Stream>? OpenReadForTest { get; set; }

    /// <summary>Test seam: substitutes the wait between transient read retries.</summary>
    internal static Action<TimeSpan>? RetryWaitForTest { get; set; }

    /// <summary>One reviewed one-sided choice and the effect the prepare reported for it.</summary>
    internal sealed record StoredChoice(string Side, string Prefix, string Action, int Files, int Folders, long Bytes);

    /// <summary>One frozen rebuildable folder: its category and the exact pair-relative path the prepare detected.</summary>
    internal sealed record StoredRebuildable(string Category, string Path);

    /// <summary>A pending inventory cursor captured by prepare or a successfully guarded apply repreview.</summary>
    internal sealed record StoredInventoryCursor(string RootId, string Cursor);

    /// <summary>
    /// One path the prepare saw on both sides, with a compact per-side identity and content stamp .
    /// Short JSON names keep the record small: a first sync can match hundreds of thousands of paths.
    /// </summary>
    internal sealed record StoredMatch([property: JsonPropertyName("p")] string Path,
        [property: JsonPropertyName("a")] string A, [property: JsonPropertyName("b")] string B);

    /// <summary>
    /// The retained prepare: the choices digest, per-choice effects, the reviewed deletes' victims, the frozen
    /// rebuildable set and the both-sides matched set. The rebuildable set is the only source the apply may draw
    /// rebuildable paths from; RebuildableChoices records the reviewed scope for inspection (the digest enforces it).
    /// The matched set is the reviewed apply's baseline for post-prepare deletion propagation.
    /// </summary>
    internal sealed record Prepared(string Digest, IReadOnlyDictionary<string, string> Paths,
        IReadOnlyList<StoredChoice> OneSided, IReadOnlyList<string> DeleteVictims, int DeleteVictimCount,
        IReadOnlyList<StoredRebuildable> Rebuildables, IReadOnlyList<HeldDeletion> HeldDeletions,
        IReadOnlyList<StoredMatch> Matched, IReadOnlyList<RebuildableChoice>? RebuildableChoices = null,
        IReadOnlyList<string>? Warnings = null)
    {
        public IReadOnlyList<SyncCliCarriedIgnoredData> CarriedIgnoredDeletes { get; init; } = [];
        [JsonIgnore] internal PrepareDatabase? Database { get; init; }
        public IReadOnlyList<StoredInventoryCursor> InventoryCursors { get; init; } = [];
        public string? InventoryScope { get; init; }
        // Narrowing changes the review digest, never the approved choices. This independent binding
        // excludes large held sets, whose authorization remains in the filtered SQLite rows.
        public string? NarrowedChoicesDigest { get; init; }
        public IReadOnlyList<string> NarrowedIgnoreRules { get; init; } = [];
        /// <summary>
        /// Set when a review action rewrote the choices without re-detecting the folders they cover (Choose folder).
        /// The retained review no longer describes what the new rule applies to, so an apply must wait for a prepare
        /// that detects folders under the new prefix.
        /// </summary>
        public bool NeedsReprepare { get; init; }
    }

    /// <summary>
    /// Records a successful prepare, replacing any earlier record. The text goes to a unique sibling first and
    /// is then moved over the record, so a reader never sees a half-written file. The delete victims are stored as a
    /// case-insensitively deduplicated, ordinally sorted list (a first sync can delete tens of thousands of items).
    /// </summary>
    public static void Save(string productRoot, string pairId, FirstSyncChoices.Parsed choices,
        IReadOnlyList<SyncCliChoiceEffectData> effects, IReadOnlyList<string> deleteVictims,
        IReadOnlyList<RebuildableFolder> rebuildables, IReadOnlyList<HeldDeletion>? heldDeletions = null,
        IReadOnlyList<MatchedPair>? matched = null, IReadOnlyList<string>? warnings = null,
        IReadOnlyList<SyncCliCarriedIgnoredData>? carriedIgnoredDeletes = null, IStateStore? store = null,
        IReadOnlyList<StoredInventoryCursor>? inventoryCursors = null, string? inventoryScope = null)
    {
        if (store is SqliteStateStore database)
        {
            var small = new Prepared(FirstSyncChoices.Digest(choices, rebuildables, heldDeletions),
                choices.Winners.ToDictionary(entry => entry.Key, entry => FirstSyncChoices.WinnerText(entry.Value), StringComparer.Ordinal),
                choices.OneSided.Select((choice, index) => new StoredChoice(FirstSyncChoices.SideText(choice.Present),
                    choice.Prefix, FirstSyncChoices.ActionText(choice.Remove), effects[index].Files,
                    effects[index].Folders, effects[index].Bytes)).ToArray(), [], 0,
                rebuildables.Select(folder => new StoredRebuildable(folder.Category, folder.Path)).ToArray(), [], [],
                choices.Rebuildables, warnings)
            {
                InventoryCursors = inventoryCursors ?? [], InventoryScope = inventoryScope
            };
            PrepareDatabase.Save(database, pairId, small, deleteVictims, heldDeletions ?? [], matched ?? [],
                carriedIgnoredDeletes ?? []);
            return;
        }
        var victims = deleteVictims.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(victim => victim, StringComparer.Ordinal).ToArray();
        var held = (heldDeletions ?? []).ToArray();
        // A path matched on both sides appears at most once; the spelling kept is the local one (Join pairs by path).
        var matchedRecords = (matched ?? [])
            .GroupBy(pair => pair.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(pair => pair.Path, StringComparer.Ordinal)
            .Select(pair => new StoredMatch(pair.Path, Content.SideStamp(pair.A), Content.SideStamp(pair.B)))
            .ToArray();
        var record = new Prepared(FirstSyncChoices.Digest(choices, rebuildables, held),
            choices.Winners.ToDictionary(entry => entry.Key, entry => FirstSyncChoices.WinnerText(entry.Value),
                StringComparer.Ordinal),
            choices.OneSided.Select((choice, index) => new StoredChoice(FirstSyncChoices.SideText(choice.Present),
                choice.Prefix, FirstSyncChoices.ActionText(choice.Remove), effects[index].Files,
                effects[index].Folders, effects[index].Bytes)).ToArray(),
            victims, victims.Length,
            rebuildables.Select(folder => new StoredRebuildable(folder.Category, folder.Path)).ToArray(), held,
            matchedRecords, choices.Rebuildables, warnings)
        {
            CarriedIgnoredDeletes = carriedIgnoredDeletes ?? [],
            InventoryCursors = inventoryCursors?.ToArray() ?? [],
            InventoryScope = inventoryScope
        };
        WritePrepared(productRoot, pairId, record);
    }

    /// <summary>
    /// Replaces or removes one scoped rebuildable rule in the retained prepare. Remove rule recomputes the digest,
    /// because the removed rule matched nothing. Choose folder rewrites the rule but does not re-detect, so it marks
    /// the retained review as needing a fresh prepare instead of rebinding the digest: the frozen folder set was
    /// detected under the old prefix and must not authorize an apply under the new one. Returns false when no usable
    /// prepare or no such rule exists. This changes only review metadata; it never deletes or moves a file.
    /// </summary>
    public static bool UpdateRebuildableRule(string productRoot, string pairId, IStateStore? store,
        RebuildableChoice current, RebuildableChoice? replacement)
    {
        var prepared = Load(productRoot, pairId, store);
        if (prepared is null) return false;
        var choices = (prepared.RebuildableChoices ?? []).ToList();
        var index = choices.FindIndex(choice => SameRule(choice, current));
        if (index < 0) return false;
        // A new prefix is a new scope; the prepare must detect its folders before an apply.
        var needsReprepare = replacement is not null;
        if (replacement is null) choices.RemoveAt(index);
        else choices[index] = new RebuildableChoice(replacement.Id, replacement.Prefix.TrimEnd('/'));
        var frozen = prepared.Rebuildables.Select(entry => new RebuildableFolder(entry.Category, entry.Path, 0, 0))
            .Where(folder => !current.Matches(folder) || (replacement is not null && replacement.Matches(folder)))
            .ToArray();
        var parsed = new FirstSyncChoices.Parsed(prepared.Paths.ToDictionary(entry => entry.Key,
                entry => entry.Value switch { "A" => Side.A, "B" => Side.B, _ => (Side?)null }, StringComparer.Ordinal),
            prepared.OneSided.Select(choice => new OneSidedChoice(choice.Prefix,
                string.Equals(choice.Side, "A", StringComparison.Ordinal) ? Side.A : Side.B,
                string.Equals(choice.Action, FirstSyncChoices.RemoveAction, StringComparison.Ordinal))).ToArray(),
            choices);
        var digest = needsReprepare ? null
            : prepared.NarrowedChoicesDigest is null
                ? FirstSyncChoices.Digest(parsed, frozen, prepared.HeldDeletions)
                : FirstSyncChoices.Digest(parsed, []);
        var marker = "'" + current.Id + "' under '" + current.Prefix.TrimEnd('/') + "/'";
        var warnings = (prepared.Warnings ?? []).Where(warning => !warning.Contains(marker, StringComparison.Ordinal)).ToArray();
        var updated = prepared with
        {
            Digest = needsReprepare ? prepared.Digest : prepared.NarrowedChoicesDigest is null ? digest! : prepared.Digest,
            NarrowedChoicesDigest = needsReprepare ? prepared.NarrowedChoicesDigest
                : prepared.NarrowedChoicesDigest is null ? null : digest,
            Rebuildables = frozen.Select(folder => new StoredRebuildable(folder.Category, folder.Path)).ToArray(),
            RebuildableChoices = choices,
            Warnings = warnings,
            NeedsReprepare = prepared.NeedsReprepare || needsReprepare
        };
        if (prepared.Database is { } database) database.UpdateSmall(updated);
        else WritePrepared(productRoot, pairId, updated);
        return true;
    }

    private static bool SameRule(RebuildableChoice left, RebuildableChoice right) =>
        string.Equals(left.Id, right.Id, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Prefix.TrimEnd('/'), right.Prefix.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Saves refreshed pending cursors after a successful repreview and its review guards, before apply effects.
    /// All frozen review fields remain unchanged. The caller supplies the already-loaded prepare.
    /// </summary>
    public static Prepared UpdatePendingInventoryCursors(string productRoot, string pairId, Prepared prepared,
        IReadOnlyDictionary<string, string> cursors, string scope)
    {
        var updated = prepared with
        {
            InventoryCursors = cursors.Select(entry => new StoredInventoryCursor(entry.Key, entry.Value)).ToArray(),
            InventoryScope = scope
        };
        if (prepared.Database is { } database) database.UpdateInventoryCursors(cursors, scope);
        else WritePrepared(productRoot, pairId, updated);
        return updated;
    }

    /// <summary>Forgets only the requested roots' pending checkpoints, preserving the frozen review.
    /// An unreadable record is reported through the callback; the caller decides whether to discard it.</summary>
    public static void ClearPendingInventoryCursors(string productRoot, string pairId,
        IReadOnlyCollection<string> rootIds, IStateStore? store = null, Action<string>? onUnreadable = null)
    {
        if (store is SqliteStateStore)
        {
            // Import a legacy prepare first, so clearing cannot resurrect its old checkpoint later.
            var current = Load(productRoot, pairId, store, onUnreadable);
            current?.Database?.ClearInventoryCursors(rootIds, current.InventoryScope);
            return;
        }
        var prepared = Load(productRoot, pairId, onUnreadable: onUnreadable);
        if (prepared is null) return;
        var roots = rootIds.ToHashSet(StringComparer.Ordinal);
        var retained = (prepared.InventoryCursors ?? [])
            .Where(entry => entry is not null && !roots.Contains(entry.RootId)).ToArray();
        WritePrepared(productRoot, pairId, prepared with { InventoryCursors = retained });
    }

    private static void WritePrepared(string productRoot, string pairId, Prepared record)
    {
        var file = FileFor(productRoot, pairId);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = File.Create(temp)) JsonSerializer.Serialize(output, record, Json);
            File.Move(temp, file, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>
    /// Opens the retained record, retrying a transient sharing or permission failure twice before giving up.
    /// Shared with the SQLite legacy import so both read paths survive a briefly held file. The caller's existing
    /// handler still decides what an exhausted read means; corruption is not transient.
    /// </summary>
    internal static Stream OpenRecord(string file)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return OpenReadForTest is { } open ? open(file) : File.OpenRead(file);
            }
            catch (Exception error) when (attempt < ReadRetryDelays.Length && IsTransientReadError(error))
            {
                var delay = ReadRetryDelays[attempt];
                if (RetryWaitForTest is { } wait) wait(delay);
                else Thread.Sleep(delay);
            }
        }
    }

    // A sharing or lock violation (Win32 32/33) and a denied access are the shapes a scanner produces briefly.
    private static bool IsTransientReadError(Exception error) =>
        error is UnauthorizedAccessException ||
        error is IOException && (error.HResult & 0xffff) is (32 or 33);

    /// <summary>The retained prepare, or null when absent/inaccessible; parse/import failures explain how to retry.
    /// A transiently locked record is retried before it is reported. A fresh prepare may discard unreadable records
    /// through the callback; database errors and unrelated I/O failures still propagate.</summary>
    public static Prepared? Load(string productRoot, string pairId, IStateStore? store = null,
        Action<string>? onUnreadable = null)
    {
        Prepared? Unreadable(string reason)
        {
            ProgressGuard.Report(onUnreadable, reason);
            return null;
        }
        var file = FileFor(productRoot, pairId);
        try
        {
            if (store is SqliteStateStore database)
            {
                if (database.ReadPrepare(pairId) is { } header)
                {
                    if (header.Digest == SqliteStateStore.NarrowingDigest) return null;
                    // Retired prototype reviews used the pair's prepare slot. They are never first-sync authority.
                    using var metadata = JsonDocument.Parse(header.MetadataJson);
                    if (metadata.RootElement.TryGetProperty("OriginReason", out _) &&
                        metadata.RootElement.TryGetProperty("Signature", out _)) return null;
                    return new PrepareDatabase(database, header).Read();
                }
                return File.Exists(file) ? PrepareDatabase.Import(database, pairId, file) : null;
            }
            if (!File.Exists(file))
                return null;
            using var input = OpenRecord(file);
            var record = JsonSerializer.Deserialize<Prepared>(input, Json);
            if (record is not { Digest: { Length: > 0 }, Paths: not null, OneSided: not null,
                DeleteVictims: not null, Rebuildables: not null, HeldDeletions: not null })
                return Unreadable("invalid prepare record shape");
            // A record written before decision 71 has no matched set; it is simply empty, so a prepare made by an
            // older build stays usable (the guard then falls back to baselines/evidence alone).
            var matched = record.Matched ?? Array.Empty<StoredMatch>();
            return record.DeleteVictims.Count == record.DeleteVictimCount &&
                record.HeldDeletions.All(held => held is { Path: not null, Rule: not null } &&
                    held.Local is Side.A or Side.B) &&
                record.OneSided.All(choice => choice is { Side: not null, Prefix: not null, Action: not null }) &&
                record.Rebuildables.All(folder => folder is { Category: not null, Path: not null }) &&
                matched.All(match => match is { Path: not null, A: not null, B: not null }) &&
                (record.CarriedIgnoredDeletes ?? []).All(item => item is
                    { Path: not null, Side: "A" or "B", Files: >= 0, Folders: >= 0, Bytes: >= 0 })
                ? record with { Matched = matched, CarriedIgnoredDeletes = record.CarriedIgnoredDeletes ?? [] }
                : Unreadable("invalid prepare record shape");
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            throw new SyncCliReviewException("The retained first-sync prepare could not be read or imported: " +
                $"SQLite error {error.SqliteErrorCode} ({error.Message}). Resolve the database error and run prepare again.");
        }
        catch (JsonException error)
        {
            if (onUnreadable is not null) return Unreadable("unparsable prepare record: " + error.Message);
            throw new SyncCliReviewException("The retained first-sync prepare could not be parsed: " +
                error.Message + " — run prepare again.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A sharing/lock or access shape that outlasted the retries is a transiently unreadable record: report
            // it so the caller can discard it. Any other I/O failure is a real fault and must propagate instead of
            // silently discarding a reviewed prepare (a rescan must not lose it on an unrelated device error).
            if (!IsTransientReadError(error)) throw;
            return Unreadable(error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>
    /// Returns the retained prepare's valid pending cursors for the exact inventory scope. Older records have
    /// none. Reading these does not advance them; only a successfully guarded repreview may refresh them.
    /// </summary>
    public static IReadOnlyDictionary<string, string> PendingInventoryCursors(Prepared prepared, string scope)
    {
        var cursors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(scope) ||
            !string.Equals(prepared.InventoryScope, scope, StringComparison.Ordinal)) return cursors;
        foreach (var entry in prepared.InventoryCursors ?? [])
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.RootId) ||
                string.IsNullOrWhiteSpace(entry.Cursor)) continue;
            cursors.TryAdd(entry.RootId, entry.Cursor);
        }
        return cursors;
    }

    /// <summary>
    /// Decisions 54/85: identifies grown remove choices to hold without refusing unrelated work.
    /// Copies and every other rule are not limited.
    /// </summary>
    public static IReadOnlyList<OneSidedChoice> GrownRemoveChoices(Prepared prepared, IReadOnlyList<OneSidedChoice> choices,
        IReadOnlyList<SyncCliChoiceEffectData> effects)
    {
        var grown = new List<OneSidedChoice>();
        var reported = prepared.OneSided.ToDictionary(
            stored => FirstSyncChoices.ChoiceKey(stored.Side, stored.Prefix), StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < choices.Count; index++)
        {
            if (!choices[index].Remove) continue;
            var now = effects[index];
            var key = FirstSyncChoices.ChoiceKey(FirstSyncChoices.SideText(choices[index].Present), choices[index].Prefix);
            if (reported.TryGetValue(key, out var then) && now.Files <= then.Files && now.Bytes <= then.Bytes)
                continue;
            grown.Add(choices[index]);
        }
        return grown;
    }

    /// <summary>
    /// Decision 54 addendum, narrowed by decision 71: refuses an apply that would carry out a guarded delete outside
    /// the reviewed prepare's set. The caller passes the guarded list from
    /// <see cref="FolderDelete.SplitVictims"/>; ordinary deletion propagation is ongoing sync. Fewer deletes is fine.
    /// </summary>
    public static void RefuseUnreviewedDeletes(Prepared prepared, IReadOnlyList<string> victims) =>
        RefuseUnreviewedDeletes(ReviewedDeleteKeys(prepared), victims);

    public static void RefuseUnreviewedDeletes(IReadOnlySet<string> shown, IReadOnlyList<string> victims)
    {
        var extra = victims.Where(victim => !shown.Contains(victim))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (extra.Length == 0) return;
        throw new SyncCliReviewException("The first sync would delete " + extra.Length + " item(s)" +
            UnreviewedDeletes + string.Join(", ", extra.Take(10)) + ".");
    }

    /// <summary>Apply uses the reviewed per-folder ceiling, never the newly scanned amount as authorization.</summary>
    public static Plan BindCarriedIgnoredBudgets(Prepared prepared, Plan plan)
    {
        var shown = prepared.Database is not null ? null : (prepared.CarriedIgnoredDeletes ?? [])
            .GroupBy(item => item.Side + ":" + item.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        return plan with { Actions = plan.Actions.Select(action =>
        {
            if (action.Kind != ActionKind.Delete || action.ExpectedTarget?.Kind != ItemKind.Folder) return action;
            // Ordinary post-prepare propagation keeps its fresh plan ceiling. First-sync choices and evidence
            // deletes must use the disclosure attached to the reviewed folder (old records authorize zero).
            if (!action.FirstSyncChoice && !action.NeedsReview) return action;
            var key = FirstSyncChoices.SideText(action.Target) + ":" + action.Path;
            var entry = prepared.Database is { } database ? database.Budget(key) :
                shown!.GetValueOrDefault(key);
            var budget = entry is not null
                ? new CarriedIgnoredContent(entry.Files, entry.Folders, entry.Bytes)
                : new CarriedIgnoredContent(0, 0, 0);
            return action with { CarriedIgnoredBudget = budget };
        }).ToArray() };
    }

    /// <summary>
    /// A held rule authorizes only its exact local victim, never descendants of a held folder. Keep the original
    /// held list frozen: a later preview may resolve some or all of it without changing the prepare digest.
    /// </summary>
    public static IReadOnlySet<string> ReviewedDeleteKeys(Prepared prepared)
    {
        if (prepared.Database is { } database) return database.ReviewedKeys;
        var reviewed = new HashSet<string>(prepared.DeleteVictims, StringComparer.OrdinalIgnoreCase);
        foreach (var held in prepared.HeldDeletions)
            reviewed.Add(FolderDelete.VictimKey(held.Local, held.Path));
        return reviewed;
    }

    /// <summary>
    /// Only the original reviewed rules may execute. New paths, sides or rules stay pending without blocking
    /// unrelated reviewed work, and never enter the execution-time held-deletion authorization set.
    /// </summary>
    public static Plan PartitionHeldDeletions(Prepared prepared, Plan plan)
    {
        var shown = prepared.Database is not null ? null : prepared.HeldDeletions.GroupBy(held => held.Rule, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.Select(held => FolderDelete.VictimKey(held.Local, held.Path))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.Ordinal);
        var reviewed = new List<HeldDeletion>();
        var pending = new List<HeldDeletion>(plan.UnreviewedHeldDeletions);
        foreach (var held in plan.HeldDeletions)
        {
            var authorized = prepared.Database is { } database ? database.ContainsHeld(held) :
                shown!.TryGetValue(held.Rule, out var paths) && paths.Contains(FolderDelete.VictimKey(held.Local, held.Path));
            if (authorized) reviewed.Add(held);
            else pending.Add(held);
        }
        return plan with { HeldDeletions = reviewed, UnreviewedHeldDeletions = pending };
    }

    /// <summary>
    /// Forgets the pair's prepare: before a new prepare (so a failed one authorizes nothing) and after the first
    /// sync completes. Both database generations and any legacy app-owned record are invalidated.
    /// </summary>
    public static void Clear(string productRoot, string pairId, IStateStore? store = null)
    {
        var file = FileFor(productRoot, pairId);
        // Remove the legacy fallback first: a crash cannot resurrect an older review after its DB head is cleared.
        if (File.Exists(file)) File.Delete(file);
        if (store is SqliteStateStore database) database.ClearPrepare(pairId);
    }

    private static string FileFor(string productRoot, string pairId) =>
        Path.Combine(productRoot, DirectoryName, FileNamePrefix + pairId + FileNameSuffix);
}
