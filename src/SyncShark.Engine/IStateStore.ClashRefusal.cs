namespace Massdex.SyncShark.Engine;

/// <summary>The repeated name-clash question of the persistence boundary.</summary>
public partial interface IStateStore
{
    /// <summary>Answers a repeated name-clash refusal for one member without writing: Covered and Attach carry the
    /// continuing open issue at the path; Raise carries none. Stale selections throw.</summary>
    (ClashRefusal State, Issue? Existing) ClashRefusalState(WorkSelection selection, string path,
        IssueSubject subject, (ChangeNotice Notice, bool IsFolder, SyncedEntry? Before) work);
}
