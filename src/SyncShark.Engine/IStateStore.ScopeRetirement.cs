namespace Massdex.SyncShark.Engine;

/// <summary>Completion of queued work that lies wholly outside the pair's exclusions.</summary>
public partial interface IStateStore
{
    /// <summary>Completes the selection without observation when its anchor key and every member's paths, saved
    /// bindings and mapped counterparts lie under exclusions or nested roots and it owes nothing else (rename custody,
    /// residual, discovery, Local dirty evidence, issue or parent ownership); returns whether it did. Ask it after
    /// pending ancestors are resolved. Stale selections throw.</summary>
    bool CompleteOutOfScope(WorkSelection selection, PathScope scope);
}
