namespace Massdex.SyncShark.V2;

public static partial class Planner
{
    // Settlement consumes normal planner output, never a second equality/decision table.
    internal static bool IsEffectFreeSettlement(Plan plan) =>
        plan.RunUnavailable is null && plan.Notices.Count == 0 && plan.SkippedActions.Count == 0 &&
        plan.DeferredPlanningActions.Count == 0 && plan.RecoveryPending.Count == 0 &&
        plan.HeldDeletions.Count == 0 && plan.UnreviewedHeldDeletions.Count == 0 &&
        plan.Actions.All(action => action.Kind == ActionKind.Adopt && !action.RecoveryFirst);
}
