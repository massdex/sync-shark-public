using System.Collections.Concurrent;
using System.Reflection;

namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static Task ReconciliationMovingEvidenceAsync()
    {
        var endpoint = new FakeEndpoint(Side.B, "cloud");
        var old = endpoint.Seed("Folder", "", ItemKind.Folder);
        var moved = old with { Path = "Moved" };
        var complete = typeof(DropboxInventory).GetMethod("CompleteMissingFolders", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var failed in new[] { "Folder/child.txt", "Moved/child.txt", "" })
        {
            var failures = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (failed.Length > 0) failures[failed] = "unreadable";
            complete.Invoke(null, [new[] { old }, new[] { new Change(ChangeKind.Upsert, moved.ObjectId,
                moved.Path, moved, null) }, failures]);
            Require(failures.ContainsKey("Folder") == (failed.Length > 0),
                "missing old alias is move evidence only if no read under either alias failed");
        }
        return Task.CompletedTask;
    }
}
