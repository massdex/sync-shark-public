using System.Reflection;

namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static Task ReconciliationMovingOrderAsync()
    {
        var target = new FakeEndpoint(Side.B, "target");
        var folder = target.Seed("Folder", "", ItemKind.Folder);
        var child = target.Seed("Folder/child.txt", "child");
        var occupant = target.Seed("Moved", "occupant");
        var move = new SyncAction("move", 25, ActionKind.Move, Side.B, "Moved", "Folder", folder, folder, false, null);
        var vacate = new SyncAction("vacate", 13, ActionKind.Delete, Side.B, "Moved", null, null, occupant, false, null);
        var delete = new SyncAction("child", 19, ActionKind.Delete, Side.B, "Moved/child.txt", null, null,
            child with { Path = "Moved/child.txt" }, false, null);
        var batch = new ReconcileBatch("unit", ["Folder", "Moved"], new HashSet<string>(), new HashSet<string>(), (_, _) => false)
        { MovingFolders = ["Folder", "Moved"], RowsB = [folder, child] };
        var protect = typeof(SyncRun).GetMethod("ProtectMovingUnit", BindingFlags.Static | BindingFlags.NonPublic)!;
        var plan = (Plan)protect.Invoke(null, [new Plan("pair", 0, [vacate, move, delete], []), batch])!;
        var steps = ActionPhases.Assign(plan.Actions);
        Require(plan.Actions.Single(a => a.ActionId == "vacate").AfterFolderMove is null &&
            steps["vacate"].Phase == ActionPhases.Vacate && steps["move"].Phase == ActionPhases.Structure &&
            plan.Actions.Single(a => a.ActionId == "child").AfterFolderMove == "move" &&
            steps["child"].Phase == ActionPhases.Remove,
            "destination occupant vacates before rename; captured child deletes only after rename");
        return Task.CompletedTask;
    }
}
