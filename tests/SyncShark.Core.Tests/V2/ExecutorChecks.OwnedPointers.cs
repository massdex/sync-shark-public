namespace Massdex.SyncShark.V2;

internal static partial class ExecutorChecks
{
    private static async Task FolderDelete_OwnedPointerDoesNotArchive()
    {
        var (store, a, b, executor) = Rig();
        var pointer = a.Seed("folder/pointer.txt", "cloud bytes", versionId: "v1") with { Flags = ItemFlags.OwnedPointer };
        var offline = a.Seed("folder/offline.txt", "local bytes", versionId: "v1");
        var folder = a.Seed("folder", "", ItemKind.Folder, versionId: "v1");
        var gen = store.BeginGeneration("ra", "snapshot");
        store.WriteSnapshot(new Snapshot("ra", Side.A, gen, "cursor", [folder, pointer, offline], []));
        store.CompleteGeneration(gen, "cursor", true);
        var record = new RecordingWriter(a);
        var action = Act(ActionKind.Delete, Side.A, folder.Path, expected: folder, rf: true);
        var results = await Apply(executor, Plan(action), record, b);
        Require(results.All(result => result.Outcome == Outcome.Done), "pointer-containing folder delete completes");
        Require(record.Calls.Contains("Delete:folder/pointer.txt") &&
            !record.Calls.Any(call => call.StartsWith("Preserve:folder/pointer.txt", StringComparison.Ordinal)),
            "expanded local pointer delete does not hydrate for Recovery");
        Require(record.Calls.Count(call => call.StartsWith("Preserve:", StringComparison.Ordinal)) == 1,
            "ordinary local child still receives its Recovery copy");
        Require(Index(record.Calls, "Delete:folder/pointer.txt") < Index(record.Calls, "Delete:folder"),
            "folder removal still waits for successful pointer deletion");
    }
}
