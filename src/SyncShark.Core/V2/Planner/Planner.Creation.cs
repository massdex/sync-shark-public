namespace Massdex.SyncShark.V2;

public static partial class Planner
{
    // This predicate governs only no-baseline creation; Known/Deleted retain their original meanings.
    internal static bool NeedsCreationObservation(PlanRow row) =>
        row.Base is null && (row.A is null) != (row.B is null) &&
        (row.A is null ? !row.Ev.KnownA && !row.Ev.DeletedA && !row.Ev.CreationObservedA :
            !row.Ev.KnownB && !row.Ev.DeletedB && !row.Ev.CreationObservedB);
}
