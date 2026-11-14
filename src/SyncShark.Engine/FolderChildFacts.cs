namespace Massdex.SyncShark.Engine;

/// <summary>Facts the Executor gathers for FolderDeletes.Classify, so the Planner needs no scope or store.
/// InThisScan: the entry was in this run's listing. Restored: this run's rows restored it as a survivor.
/// Unreadable: the child's local path is under known unreadable content. KeepsMount: the child is a shared-folder
/// mount the removal's authority must keep (FolderDeletes.KeepsMount).</summary>
public readonly record struct FolderChildFacts(
    bool InThisScan, bool InScope, bool HoldsNestedRoot,
    bool Restored, bool Unreadable, bool KeepsMount = false);
