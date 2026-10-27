using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

// Run this identical fixture on the base and candidate; compare the serialized planner-byte digest.
internal static class CompareEquivalenceChecks
{
    internal static void Run()
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var random = new Random(9137);
        for (var sample = 0; sample < 128; sample++)
        {
            using var store = new InMemoryStateStore();
            var pair = new PairDef("pair", "Pair A", new("left", "synthetic", null, "/left", "left"),
                new("right", "synthetic", null, "/right", "right"), [], [], true);
            var a = new List<ItemState>();
            var b = new List<ItemState>();
            var baselines = new List<Baseline>();
            for (var i = 0; i < 64; i++)
            {
                ItemState Item(string side) => new($"Parent/file-{i:D3}.txt", ItemKind.File,
                    $"{side}-{i:D3}", null, new string('a', 64), 1, DateTimeOffset.UnixEpoch, ItemFlags.None);
                var left = Item("a");
                var right = Item("b");
                baselines.Add(new(pair.PairId, $"key-{i:D3}", left, right, 1));
                if (random.Next(6) != 0) a.Add(random.Next(4) == 0 ? left with { Path = $"Moved/a-{i}.txt" } : left);
                if (random.Next(6) != 0) b.Add(random.Next(4) == 0 ? right with { ObjectId = $"replacement-{i}" } : right);
                if (random.Next(7) == 0) a.Add(left with { ObjectId = $"extra-{i}", Path = left.Path.ToUpperInvariant() });
            }
            if ((sample & 1) != 0) baselines.Reverse();
            var held = sample % 4 == 0 ? Array.Empty<string>() :
                new[] { $"Parent/file-{sample % 64:D3}.txt", $"Moved/a-{(sample + 7) % 64}.txt" };
            var loader = new RunScope(store, p => !p.EndsWith("009.txt"), _ => true,
                LinkExclusions.None.WithPending(held));
            var prepare = typeof(RunScope).GetMethod("Prepare", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var scope = (RunScope.Scope)prepare.Invoke(loader,
                [pair, a, b, baselines, Array.Empty<ItemState>(), Array.Empty<ItemState>(), "Full", null])!;
            var rows = Planner.Join(scope.Baselines, scope.A, scope.B,
                new Dictionary<string, DateTimeOffset?>(), new Dictionary<string, DateTimeOffset?>(),
                new Completeness(true, true, new HashSet<string>(), new HashSet<string>()), false);
            var options = new PlanOptions(TimeSpan.Zero, false,
                new LabelRule("{name} [{side}]{ext}", "s", TimeSpan.Zero, "A", "B"), int.MaxValue);
            var plan = Planner.Decide(pair.PairId, 2, rows, options);
            digest.AppendData(JsonSerializer.SerializeToUtf8Bytes(rows));
            digest.AppendData(JsonSerializer.SerializeToUtf8Bytes(plan));
        }
        Console.WriteLine($"compare-equivalence-samples=128; serialized-sha256={Convert.ToHexString(digest.GetHashAndReset())}");
    }
}
