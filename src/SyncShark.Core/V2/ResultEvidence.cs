namespace Massdex.SyncShark.V2;

/// <summary>Evidence required before publishing a completed baseline, independent of storage or providers.</summary>
public static class ResultEvidence
{
    public static bool CommitOnly(SyncAction action) =>
        action.Kind is ActionKind.Forget or ActionKind.Adopt or ActionKind.Hold;

    public static ActionResult Normalize(SyncAction action, ActionResult result)
    {
        if (CommitOnly(action) && result.Outcome == Outcome.Uncertain)
            return result with { Outcome = Outcome.Failed };
        if (result.Outcome != Outcome.Done || action.SuppressReconciledBaseline ||
            action.Kind is ActionKind.Delete or ActionKind.Forget or ActionKind.Hold) return result;
        var target = (action.Target == Side.A ? result.NewA : result.NewB) ?? action.ExpectedTarget;
        var other = (action.Target == Side.A ? result.NewB : result.NewA) ?? action.Source;
        if (target is { Kind: ItemKind.File, Hash: null } || other is { Kind: ItemKind.File, Hash: null } ||
            target is not null && other is not null && (target.Kind != other.Kind ||
                action.Kind != ActionKind.Move && Content.SameContent(target, other) != true))
            return result with { Outcome = Outcome.Failed, Reason = ExecutionReasons.BaselineUnconfirmed.Detail };
        return result;
    }
}
