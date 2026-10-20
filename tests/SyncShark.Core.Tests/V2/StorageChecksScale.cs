using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Massdex.SyncShark.V2;

/// <summary>The size projection to 549k paths, and (outside the suite) the 50k-row snapshot write time.</summary>
internal static class StorageChecksScale
{
    private static readonly DateTimeOffset T0 = new(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);

    /// <summary>The size projection check that belongs to the storage suite.</summary>
    public static void Run(string root) => SizeProjection(root);

    /// <summary>The 50k-row snapshot write-time measurement, run on its own so the suite stays quick.</summary>
    public static void RunThroughput(string root) => SnapshotWriteTime(root);

    // Measures everything that stays after the first sync (live rows, baselines, folders; resolved intents are deleted
    // at commit, , then the cost of each intent decision 44 keeps (failed updates).
    private static void SizeProjection(string root)
    {
        const int paired = 2000;
        const int kept = 20;
        var watch = Stopwatch.StartNew();
        var folder = Fresh(root, "size");
        var dbPath = Path.Combine(folder, "v2", "state.db");
        using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, new MutableClock());
        store.SavePair(Pair("p1"));
        var schemaBytes = store.LiveBytes();
        var aItems = new ItemState[paired];
        var bItems = new ItemState[paired];
        for (var i = 0; i < paired; i++)
        {
            var path = ItemPath(i);
            aItems[i] = new ItemState(path, ItemKind.File, "id:local:0000abcd:" + i.ToString("x16"),
                "rev" + i.ToString("x14"), Hash(i), 4096, T0, ItemFlags.None);
            bItems[i] = aItems[i] with { ObjectId = "id:" + i.ToString("D22"), VersionId = "rev" + (i + 7).ToString("x14") };
        }
        var genA = store.BeginGeneration("ra", "snapshot");
        store.WriteSnapshot(new Snapshot("ra", Side.A, genA, "c", aItems, []));
        var genB = store.BeginGeneration("rb", "snapshot");
        store.WriteSnapshot(new Snapshot("rb", Side.B, genB, "c", bItems, []));
        for (var i = 0; i < paired; i++)
        {
            var action = new SyncAction("a" + i, 3, ActionKind.Adopt, Side.B, ItemPath(i), null, null, null, false, "k" + i);
            store.CommitResult("p1", 1, action,
                new ActionResult(action.ActionId, Outcome.Done, aItems[i], bItems[i], null, null));
        }
        Require(store.IntentRows() == 0, "no resolved intent remains stored after the first sync");
        var bytes = store.LiveBytes();
        for (var i = 0; i < kept; i++)
        {
            var update = new SyncAction("u" + i, 2, ActionKind.Update, Side.B, ItemPath(i), null, aItems[i], bItems[i],
                false, "k" + i);
            store.RecordIntent("p1", update);
            store.CommitResult("p1", 1, update, new ActionResult(update.ActionId, Outcome.Failed, null, null, null, "x"));
        }
        var perIntent = (double)(store.LiveBytes() - bytes) / kept;
        var average = Enumerable.Range(0, paired).Average(i => (double)ItemPath(i).Length);
        // The empty schema's pages are a fixed cost, not a per-path one; at 549k paths they are negligible.
        var perPath = (double)(bytes - schemaBytes) / paired;
        var projected = (long)(perPath * 549_000.0) + schemaBytes;
        Console.WriteLine(
            $"size: {bytes} live bytes ({schemaBytes} of them the empty schema) for {paired} paired paths " +
            $"({paired * 2} live rows, {paired} baselines, " +
            $"{paired / 10} folders, 0 stored intents); average path {average:F1} chars; {perPath:F0} bytes per " +
            $"paired path; 549k projection {projected / (1024.0 * 1024):F0} MiB; each kept (failed) intent adds " +
            $"{perIntent:F0} bytes; file {new FileInfo(dbPath).Length} bytes; {watch.Elapsed.TotalSeconds:F1} s");
        Require(average >= 130, $"the size fixture must use realistic paths, got {average:F1} chars");
        Require(projected <= 500L * 1024 * 1024,
            $"the 549k-path projection must stay under 500 MiB, got {projected / (1024.0 * 1024):F0} MiB");
    }

    private static void SnapshotWriteTime(string root)
    {
        const int count = 50_000;
        var folder = Fresh(root, "throughput");
        using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, new MutableClock());
        var items = new ItemState[count];
        for (var i = 0; i < count; i++)
            items[i] = new ItemState(ItemPath(i), ItemKind.File, "id:local:0000abcd:" + i.ToString("x16"),
                "rev" + i.ToString("x14"), Hash(i), 4096, T0, ItemFlags.None);
        var gen = store.BeginGeneration("root", "snapshot");
        var watch = Stopwatch.StartNew();
        store.WriteSnapshot(new Snapshot("root", Side.A, gen, "c", items, []));
        watch.Stop();
        store.CompleteGeneration(gen, "c", true);
        var projected = watch.Elapsed.TotalSeconds * (1_020_000.0 / count);
        Console.WriteLine(
            $"throughput: a {count:N0}-row snapshot took {watch.Elapsed.TotalSeconds:F2} s on F: (USB); " +
            $"projection to 1.02M local entries = {projected:F1} s");
        Require(watch.Elapsed < TimeSpan.FromMinutes(2), "a 50k-row snapshot must not be pathological");
    }

    /// <summary>Ten items per deep folder, so the folder table stays about a tenth of the item count.</summary>
    private static string ItemPath(int index) => DeepFolder(index / 10) + "/" + Name(index);

    private static string DeepFolder(int index)
    {
        var builder = new StringBuilder();
        var components = index % 2 == 0 ? 10 : 11;
        for (var n = 0; n < components; n++)
        {
            if (n > 0) builder.Append('/');
            builder.Append('s').Append(((long)index * 31 + n).ToString("D10"));
        }
        return builder.ToString();
    }

    private static string Name(int index) => "fi" + index.ToString("D6") + ".x" + ((long)index * 7).ToString("D9");

    private static string Hash(int index) =>
        Convert.ToHexString(SHA256.HashData(BitConverter.GetBytes((long)index))).ToLowerInvariant();

    private static string Fresh(string root, string name)
    {
        var folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static PairDef Pair(string id) => new(id, id,
        new RootDef("ra", "Local", null, "C:\\a", "id:local:1"),
        new RootDef("rb", "Dropbox", "acc", "/b", "id:dbx:1"),
        Array.Empty<string>(), Array.Empty<string>(), true);

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }

    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = T0;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
