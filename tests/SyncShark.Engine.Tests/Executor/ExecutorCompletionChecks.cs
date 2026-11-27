using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Executor cases completed after the first integrated run, for files owned by other writers.</summary>
public static class ExecutorCompletionChecks
{
    /// <summary>A Dropbox-side no-keep apply never bins or overwrites a PC edit, whether it lands before the replacement acts or after it settled.</summary>
    public static async Task ReplacedFilesEditLegsAsync()
    {
        foreach (var leg in new[] { "early-edit", "later-edit" })
        {
            var rule = new Exclusion(Side.Dropbox, "R", false);
            var pair = ItemChecks.Pair() with { Exclusions = [rule] };
            using var store = new InMemoryStateStore();
            await store.SavePairAsync(pair, default, [(pair.PairId, new Scope("R", "R"))]);
            pair = (await store.PairAsync(pair.PairId, default))!;
            var pc = new FakeSide(Side.Local); var db = new FakeSide(Side.Dropbox) { IdleBoundary = new([], "cursor", null) };
            var folder = ItemChecks.File(Side.Local, "R", 0, "pc-R", true); var peer = ItemChecks.File(Side.Dropbox, "R", 0, "db-R", true);
            pc.Seed(folder, []); db.Seed(peer, []); await store.CommitAsync(pair.PairId, null, SyncedEntries.From(folder, peer), null, null, default);
            foreach (var name in new[] { "R/f1", "R/f2", "R/f3" })
            {
                var left = ItemChecks.File(Side.Local, name, 1, "pc-" + name); var right = ItemChecks.File(Side.Dropbox, name, 1, "db-" + name);
                await store.CommitAsync(pair.PairId, null, SyncedEntries.From(left, right), null, null, default);
                db.Seed(ItemChecks.File(Side.Dropbox, name, 2, right.Id), [2]); // changed on Dropbox since the saved baseline
                pc.Seed(left, [1]);
            }
            var edited = leg == "early-edit" ? "R/f2" : "R/f1";
            void Edit(byte value) => pc.Items[edited] = (ItemChecks.File(Side.Local, edited, value, pc.Items[edited].Entry.Id), [value]);
            // leg 4: "change one PC victim's bytes after SelectAsync's proof and before the RecycleBin byte check (inject at the fixture's remove seam)";
            // the fake's replacing publish is that seam: its exact-victim check refuses a changed file as the real byte check does.
            var local = FolderDeleteChecks.Observe.On(pc, (op, args) => { if (leg == "early-edit" && op == nameof(ISide.PublishAsync) && args[1] is Entry { Path: "R/f2" }) Edit(9); return null; });
            // leg 5: "inject at the fixture's Dropbox-side root delete seam ... before the delete acts".
            var cloud = FolderDeleteChecks.Observe.On(db, (op, args) => { if (leg == "later-edit" && op == nameof(ISide.DeleteAsync) && args[0] is Entry { Path: "R" }) Edit(7); return null; });
            var outcome = await new Exclusions().ApplyAsync(pair, ItemChecks.Settings, _ => { }, new PathScope(pair, [pair], DriveType.Fixed), rule, local, cloud, store,
                (_, _, _, _) => Task.CompletedTask, (_, _, _, _) => Task.CompletedTask, default, _ => [], _ => false);
            bool Holds(string name, byte value) => pc.Items.TryGetValue(name, out var v) && v.Bytes.SequenceEqual(new[] { value });
            bool Binned(byte value) => pc.History.Any(v => v.Bytes.SequenceEqual(new[] { value }));
            if (leg == "early-edit")
                // "Expect that item not replaced: the edited PC file still at its path with the edited bytes, nothing of it in the fake Recycle Bin, the Dropbox source and root intact, the apply not Applied."
                Check.Require(outcome.Outcome != ExclusionOutcome.Applied && Holds("R/f2", 9) && !Binned(9) && db.Items.ContainsKey("R") && db.Items["R/f2"].Bytes.SequenceEqual(new byte[] { 2 }) && db.Requests[nameof(ISide.DeleteAsync)] == 0,
                    $"An early PC edit was replaced, binned, or the Dropbox root went: {outcome.Outcome}");
            else
                // "Expect Applied, the edited bytes still at that PC path after the apply, the old PC version in the fake Recycle Bin, and the Dropbox root removed. Nothing of the edit is lost or binned."
                Check.Require(outcome.Outcome == ExclusionOutcome.Applied && Holds("R/f1", 7) && !Binned(7) && pc.History.Count(v => v.Bytes.SequenceEqual(new byte[] { 1 })) == 3 && Holds("R/f2", 2) && Holds("R/f3", 2) && !db.Items.ContainsKey("R"),
                    $"A later PC edit was lost or binned, or the apply did not settle: {outcome.Outcome}");
        }
    }
}
