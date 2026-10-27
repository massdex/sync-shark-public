namespace Massdex.SyncShark.V2;

/// <summary>Assigns every planned action a phase and level so effects run in a safe order (executor.md section 2).</summary>
public static class ActionPhases
{
    /// <summary>The ordered phases: commit-only, vacate, structure, content, remove.</summary>
    public const int CommitOnly = 0, Vacate = 1, Structure = 2, Content = 3, Remove = 4;

    /// <summary>Phase and level per action id; Hold actions are not assigned.</summary>
    public static IReadOnlyDictionary<string, Step> Assign(IReadOnlyList<SyncAction> actions)
    {
        var steps = new Dictionary<string, Step>(StringComparer.Ordinal);
        var creates = new CreateFootprint(actions);
        var replacements = actions.Where(action => action.Kind == ActionKind.Delete && action.AfterFolderMove is not null)
            .Select(action => (action.Target, PathKey.Of(action.Path))).ToHashSet();
        foreach (var action in actions)
            if (StepFor(action, creates) is { } step)
                steps[action.ActionId] = action.AfterFolderMove is not null && IsCreate(action.Kind) &&
                    replacements.Contains((action.Target, PathKey.Of(action.Path)))
                    ? new Step(Remove, PairPath.Depth(action.Path)) : step;
        return steps;
    }

    /// <summary>True for the kinds that place an item at a path (a Move's destination included).</summary>
    public static bool IsCreate(ActionKind kind) =>
        kind is ActionKind.CopyNew or ActionKind.MkDir or ActionKind.KeepBoth or ActionKind.Move;

    private static Step? StepFor(SyncAction action, CreateFootprint creates) => action.Kind switch
    {
        ActionKind.Adopt or ActionKind.Forget => new Step(CommitOnly, 0),
        ActionKind.MkDir or ActionKind.Move => new Step(Structure, PairPath.Depth(action.Path)),
        ActionKind.CopyNew or ActionKind.Update or ActionKind.KeepBoth => new Step(Content, 0),
        // A vacating delete is ordered deepest-first too, so an expanded folder's descendants run before its own
        // (now empty) delete, and all of them run before the create that replaces the folder.
        ActionKind.Delete => new Step(action.AfterFolderMove is null && creates.IsVacating(action) ? Vacate : Remove,
            -PairPath.Depth(action.Path)),
        _ => null,
    };

    /// <summary>Per side, the create paths and every folder above them, so a delete's vacating test is a lookup.</summary>
    private sealed class CreateFootprint
    {
        private readonly HashSet<string>[] paths = { new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase) };
        private readonly HashSet<string>[] covered = { new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase) };

        public CreateFootprint(IEnumerable<SyncAction> actions)
        {
            foreach (var create in actions.Where(a => IsCreate(a.Kind)))
            {
                paths[(int)create.Target].Add(create.Path);
                covered[(int)create.Target].Add(create.Path);
                covered[(int)create.Target].UnionWith(PairPath.Ancestors(create.Path));
            }
        }

        // A file↔folder change (rule 26) or an overwritten destination (rule 13) deletes first so its create can land.
        // A create at (or under) this path also forces the delete first: it either replaces this folder or writes inside it.
        public bool IsVacating(SyncAction delete) =>
            covered[(int)delete.Target].Contains(delete.Path) ||
            PairPath.Ancestors(delete.Path).Any(paths[(int)delete.Target].Contains);
    }
}
