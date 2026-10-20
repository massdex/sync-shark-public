using System.Collections;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Massdex.SyncShark.V2;

/// <summary>Generation-bound, bounded-memory views of a reviewed prepare. All SQL stays in the state store.</summary>
internal sealed partial class PrepareDatabase(SqliteStateStore store, PrepareHeader header)
{
    private readonly SqliteStateStore _store = store;
    private readonly PrepareHeader _header = header;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    internal const string Matches = "matched", Victims = "deleteVictims", Held = "heldDeletions",
        Budgets = "carriedIgnoredDeletes", Reviewed = "reviewed", HeldRules = "heldRules";
    internal static bool IsLarge(string name) => name is Matches or Victims or Held or Budgets;

    internal FirstSyncChoicesState.Prepared Read()
    {
        var small = JsonSerializer.Deserialize<FirstSyncChoicesState.Prepared>(_header.MetadataJson, Json)
            ?? throw new JsonException("Missing prepare metadata.");
        if (!ValidSmall(small) || !string.Equals(small.Digest, _header.Digest, StringComparison.Ordinal) ||
            Count(Victims) != small.DeleteVictimCount) throw new JsonException("Invalid prepare metadata or counts.");
        var cursors = ReadInventoryCursors(out var scope);
        return small with
        {
            Matched = new Rows<FirstSyncChoicesState.StoredMatch>(this, Matches),
            DeleteVictims = new Rows<string>(this, Victims),
            HeldDeletions = new Rows<HeldDeletion>(this, Held),
            CarriedIgnoredDeletes = new Rows<SyncCliCarriedIgnoredData>(this, Budgets),
            Database = this,
            InventoryCursors = cursors,
            InventoryScope = scope
        };
    }

    // Scope travels with each root's pending checkpoint, never in a rewritten prepare document.
    private sealed record InventoryCheckpoint(string Scope, string Cursor);

    private IReadOnlyList<FirstSyncChoicesState.StoredInventoryCursor> ReadInventoryCursors(out string? scope)
    {
        scope = null;
        var result = new List<FirstSyncChoicesState.StoredInventoryCursor>(2);
        if (_store.Pair(_header.PairId) is not { } pair) return result;
        foreach (var root in new[] { pair.A.RootId, pair.B.RootId })
        {
            var json = _store.PrepareCursorForPair(_header.PairId, root, _header.Generation, _header.Digest);
            if (json is null) continue;
            InventoryCheckpoint? checkpoint;
            try { checkpoint = JsonSerializer.Deserialize<InventoryCheckpoint>(json, Json); }
            catch (JsonException) { continue; } // Unusable checkpoint means a full scan, never authorization.
            if (checkpoint is null || string.IsNullOrWhiteSpace(checkpoint.Scope)) continue;
            if (scope is not null && scope != checkpoint.Scope) return [];
            scope = checkpoint.Scope;
            if (!string.IsNullOrWhiteSpace(checkpoint.Cursor)) result.Add(new(root, checkpoint.Cursor));
        }
        return result;
    }

    internal void UpdateInventoryCursors(IReadOnlyDictionary<string, string> cursors, string scope)
    {
        if (_store.Pair(_header.PairId) is not { } pair) return;
        foreach (var root in new[] { pair.A.RootId, pair.B.RootId })
            _store.SetPrepareCursor(_header.PairId, root,
                cursors.TryGetValue(root, out var cursor) && !string.IsNullOrWhiteSpace(cursor) &&
                !string.IsNullOrWhiteSpace(scope) ? JsonSerializer.Serialize(new InventoryCheckpoint(scope, cursor), Json) : null,
                _header.Generation, _header.Digest);
    }

    internal void ClearInventoryCursors(IEnumerable<string> roots, string? scope)
    {
        foreach (var root in roots)
            _store.SetPrepareCursor(_header.PairId, root,
                string.IsNullOrWhiteSpace(scope) ? null : JsonSerializer.Serialize(new InventoryCheckpoint(scope, ""), Json),
                _header.Generation, _header.Digest);
    }

    private static void WriteInventoryCursors(SqliteStateStore.PrepareWriter writer,
        FirstSyncChoicesState.Prepared small)
    {
        if (string.IsNullOrWhiteSpace(small.InventoryScope)) return;
        foreach (var entry in small.InventoryCursors ?? [])
            if (entry is not null && !string.IsNullOrWhiteSpace(entry.RootId) && !string.IsNullOrWhiteSpace(entry.Cursor))
                writer.SetCursor(entry.RootId, JsonSerializer.Serialize(new InventoryCheckpoint(small.InventoryScope, entry.Cursor), Json));
    }

    internal IReadOnlyDictionary<string, PrepareMatch> MatchLookup => new MatchDictionary(this);
    internal IReadOnlySet<string> ReviewedKeys => new ReviewedSet(this);
    internal bool ContainsHeld(HeldDeletion held) => Find<HeldDeletion>(HeldRules, HeldKey(held)) is not null;
    internal SyncCliCarriedIgnoredData? Budget(string key) => Find<SyncCliCarriedIgnoredData>(Budgets, key);
    private int Count(string kind) => checked((int)_store.CountPrepareRows(_header.Generation, kind, _header.Digest));
    private T? Find<T>(string kind, string key) => _store.FindPrepareRow(_header.Generation, kind, key, _header.Digest) is { } row
        ? JsonSerializer.Deserialize<T>(row.Json, Json) : default;

    internal static string HeldKey(HeldDeletion held) =>
        Convert.ToHexString(Encoding.UTF8.GetBytes(held.Rule)) + ":" + FolderDelete.VictimKey(held.Local, held.Path);

    /// <summary>Read old JSON once, entry by entry, into an unpublished generation before atomic publication.</summary>
    internal static FirstSyncChoicesState.Prepared Import(SqliteStateStore store, string pairId, string file)
    {
        var header = WritePrepare(store, pairId, writer =>
        {
            using var stream = FirstSyncChoicesState.OpenRecord(file);
            var reader = new PrepareJsonReader(stream);
            if (reader.Token().Token != JsonTokenType.StartObject) throw new JsonException("Expected prepare object.");
            var metadata = new JsonObject();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            long reviewed = 0, heldRules = 0;
            while (true)
            {
                var token = reader.Token();
                if (token.Token == JsonTokenType.EndObject) break;
                if (token.Token != JsonTokenType.PropertyName || !seen.Add(token.Text!))
                    throw new JsonException("Invalid or repeated prepare property.");
                var name = token.Text!;
                if (!IsLarge(name))
                {
                    using var value = reader.ValueOrArrayEnd() ?? throw new JsonException("Missing prepare value.");
                    metadata[name] = JsonNode.Parse(value.RootElement.GetRawText());
                    continue;
                }
                var start = reader.Token().Token;
                // Pre-decision-71 records may have no matched array (or explicitly null).
                if (start == JsonTokenType.Null && name is Matches or Budgets)
                {
                    metadata[name] = new JsonArray();
                    continue;
                }
                if (start != JsonTokenType.StartArray) throw new JsonException("Expected prepare array.");
                long ordinal = 0;
                while (true)
                {
                    using var value = reader.ValueOrArrayEnd();
                    if (value is null) break;
                    var json = value.RootElement.GetRawText();
                    var keys = ValidateKeys(name, value.RootElement);
                    if (name == Matches)
                    {
                        if (!writer.AddUniqueValidated(name, ordinal, keys.Key, json)) throw new JsonException("Duplicate match path.");
                    }
                    else writer.AddValidated(name, ordinal, keys.Key, json);
                    AddAuthorization(writer, name, keys, json, ref reviewed, ref heldRules);
                    ordinal++;
                }
                counts[name] = ordinal;
                metadata[name] = new JsonArray();
            }
            if (reader.Token().Token != JsonTokenType.None) throw new JsonException("Trailing prepare content.");
            var small = metadata.Deserialize<FirstSyncChoicesState.Prepared>(Json)
                ?? throw new JsonException("Missing prepare metadata.");
            if (!ValidSmall(small) || !counts.TryGetValue(Victims, out var victims) || victims != small.DeleteVictimCount ||
                !counts.ContainsKey(Held)) throw new JsonException("Invalid prepare metadata or counts.");
            WriteInventoryCursors(writer, small);
            metadata.Remove("inventoryCursors");
            metadata.Remove("inventoryScope");
            writer.Commit(metadata.ToJsonString(Json), small.Digest);
        });
        return new PrepareDatabase(store, header).Read();
    }

    internal static void Save(SqliteStateStore store, string pairId, FirstSyncChoicesState.Prepared small,
        IEnumerable<string> victims, IEnumerable<HeldDeletion> held, IEnumerable<MatchedPair> matches,
        IEnumerable<SyncCliCarriedIgnoredData> budgets)
    {
        WritePrepare(store, pairId, writer =>
        {
            long reviewed = 0, heldRules = 0, ordinal = 0;
            foreach (var victim in victims)
            {
                var json = JsonSerializer.Serialize(victim, Json);
                if (!writer.AddUniqueValidated(Victims, ordinal, victim, json)) continue;
                ordinal++;
                AddAuthorization(writer, Victims, new(victim, null), json, ref reviewed, ref heldRules);
            }
            var victimCount = checked((int)ordinal);
            ordinal = 0;
            foreach (var item in held)
            {
                var json = JsonSerializer.Serialize(item, Json);
                if (item is not { Path: not null, Rule: not null } || item.Local is not (Side.A or Side.B))
                    throw new JsonException("Invalid held deletion.");
                var keys = new RowKeys(FolderDelete.VictimKey(item.Local, item.Path), HeldKey(item));
                writer.AddValidated(Held, ordinal++, keys.Key, json);
                AddAuthorization(writer, Held, keys, json, ref reviewed, ref heldRules);
            }
            ordinal = 0;
            foreach (var pair in matches)
            {
                var match = new FirstSyncChoicesState.StoredMatch(pair.Path, Content.SideStamp(pair.A), Content.SideStamp(pair.B));
                if (writer.AddUniqueValidated(Matches, ordinal, match.Path, JsonSerializer.Serialize(match, Json))) ordinal++;
            }
            ordinal = 0;
            foreach (var budget in budgets)
            {
                var json = JsonSerializer.Serialize(budget, Json);
                if (budget is not { Path: not null, Side: "A" or "B", Files: >= 0, Folders: >= 0, Bytes: >= 0 })
                    throw new JsonException("Invalid carried budget.");
                writer.AddValidated(Budgets, ordinal++, budget.Side + ":" + budget.Path, json);
            }
            WriteInventoryCursors(writer, small);
            writer.Commit(JsonSerializer.Serialize(small with
            {
                DeleteVictimCount = victimCount, InventoryCursors = [], InventoryScope = null
            }, Json), small.Digest);
        });
    }

    private static PrepareHeader WritePrepare(SqliteStateStore store, string pairId,
        Action<SqliteStateStore.PrepareWriter> write)
    {
        try { return store.WritePrepare(pairId, write); }
        catch (PreparePublicationException error)
        {
            throw new SyncCliReviewException("The first-sync prepare could not be published: " +
                error.Message + " Review the pair and run prepare again.");
        }
    }

    private readonly record struct RowKeys(string Key, string? HeldRuleKey);

    private static void AddAuthorization(SqliteStateStore.PrepareWriter writer, string kind, RowKeys keys, string json,
        ref long reviewed, ref long heldRules)
    {
        if (kind is not (Victims or Held)) return;
        if (keys.HeldRuleKey is { } rule && writer.AddUniqueValidated(HeldRules, heldRules, rule, json)) heldRules++;
        if (writer.AddUniqueValidated(Reviewed, reviewed, keys.Key, JsonSerializer.Serialize(keys.Key, Json))) reviewed++;
    }

    // The importer already parsed this document. Check its typed fields directly, once, then reuse the keys
    // for both storage and authorization rows; do not deserialize/parse the same JSON again in each layer.
    private static RowKeys ValidateKeys(string kind, JsonElement value)
    {
        if (kind == Victims) return new(Text(value), null);
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("Expected prepare row object.");
        if (kind == Matches)
        {
            var path = FieldText(value, "p");
            _ = FieldText(value, "a");
            _ = FieldText(value, "b");
            return new(path, null);
        }
        var itemPath = FieldText(value, "path");
        if (kind == Held)
        {
            var localNumber = Number(value, "local");
            if (localNumber != (int)Side.A && localNumber != (int)Side.B) throw new JsonException("Invalid held side.");
            var rule = value.TryGetProperty("rule", out var ruleValue) ? Text(ruleValue) : HeldDeletion.NewerDeletionRule;
            var held = new HeldDeletion(itemPath, (Side)localNumber, rule);
            return new(FolderDelete.VictimKey(held.Local, itemPath), HeldKey(held));
        }
        if (kind == Budgets)
        {
            var side = FieldText(value, "side");
            var files = Number(value, "files");
            var folders = Number(value, "folders");
            var bytes = Number(value, "bytes");
            if (side is not ("A" or "B") || files < 0 || files > int.MaxValue ||
                folders < 0 || folders > int.MaxValue || bytes < 0) throw new JsonException("Invalid carried budget.");
            return new(side + ":" + itemPath, null);
        }
        throw new JsonException("Unknown prepare collection.");
    }

    private static string FieldText(JsonElement item, string name) => item.TryGetProperty(name, out var value)
        ? Text(value) : throw new JsonException("Missing prepare field: " + name);
    private static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? value.GetString()! : throw new JsonException("Expected prepare text.");
    private static long Number(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value)) return 0; // Same numeric default as the legacy record constructor.
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number : throw new JsonException("Invalid prepare number: " + name);
    }

    private static bool ValidSmall(FirstSyncChoicesState.Prepared record) =>
        record is { Digest.Length: > 0, Paths: not null, OneSided: not null, Rebuildables: not null } &&
        record.OneSided.All(choice => choice is { Side: not null, Prefix: not null, Action: not null }) &&
        record.Rebuildables.All(folder => folder is { Category: not null, Path: not null });

    private sealed class Rows<T>(PrepareDatabase owner, string kind) : IReadOnlyList<T>
    {
        public int Count { get; } = owner.Count(kind);
        public T this[int index]
        {
            get
            {
                if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
                long after = -1;
                var remaining = index;
                while (true)
                {
                    var page = owner._store.ReadPrepareRows(owner._header.Generation, kind, after, 256, owner._header.Digest);
                    if (page.Count == 0) throw new InvalidOperationException("The prepare generation is no longer available.");
                    if (remaining < page.Count) return JsonSerializer.Deserialize<T>(page[remaining].Json, Json)!;
                    remaining -= page.Count;
                    after = page[^1].Ordinal;
                }
            }
        }
        public IEnumerator<T> GetEnumerator()
        {
            long after = -1;
            while (true)
            {
                var page = owner._store.ReadPrepareRows(owner._header.Generation, kind, after, 256, owner._header.Digest);
                if (page.Count == 0) yield break;
                foreach (var row in page)
                {
                    after = row.Ordinal;
                    yield return JsonSerializer.Deserialize<T>(row.Json, Json)!;
                }
            }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class MatchDictionary(PrepareDatabase owner) : IReadOnlyDictionary<string, PrepareMatch>
    {
        public int Count { get; } = owner.Count(Matches);
        public PrepareMatch this[string key] => TryGetValue(key, out var match) ? match : throw new KeyNotFoundException();
        public IEnumerable<string> Keys => this.Select(entry => entry.Key);
        public IEnumerable<PrepareMatch> Values => this.Select(entry => entry.Value);
        public bool ContainsKey(string key) => owner.Find<FirstSyncChoicesState.StoredMatch>(Matches, key) is not null;
        public bool TryGetValue(string key, out PrepareMatch value)
        {
            var match = owner.Find<FirstSyncChoicesState.StoredMatch>(Matches, key);
            value = match is null ? null! : new(match.A, match.B);
            return match is not null;
        }
        public IEnumerator<KeyValuePair<string, PrepareMatch>> GetEnumerator()
        {
            foreach (var match in new Rows<FirstSyncChoicesState.StoredMatch>(owner, Matches))
                yield return new(match.Path, new(match.A, match.B));
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ReviewedSet(PrepareDatabase owner) : IReadOnlySet<string>
    {
        public int Count { get; } = owner.Count(Reviewed);
        public bool Contains(string key) => owner.Find<string>(Reviewed, key) is not null;
        public IEnumerator<string> GetEnumerator() => new Rows<string>(owner, Reviewed).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public bool IsSubsetOf(IEnumerable<string> other) => Snapshot().IsSubsetOf(other);
        public bool IsSupersetOf(IEnumerable<string> other) => other.All(Contains);
        public bool IsProperSupersetOf(IEnumerable<string> other) => Snapshot().IsProperSupersetOf(other);
        public bool IsProperSubsetOf(IEnumerable<string> other) => Snapshot().IsProperSubsetOf(other);
        public bool Overlaps(IEnumerable<string> other) => other.Any(Contains);
        public bool SetEquals(IEnumerable<string> other) => Snapshot().SetEquals(other);
        private HashSet<string> Snapshot() => new(this, StringComparer.OrdinalIgnoreCase);
    }
}
