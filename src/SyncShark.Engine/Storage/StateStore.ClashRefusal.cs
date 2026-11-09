namespace Massdex.SyncShark.Engine.Storage;

/// <summary>The ordered boundary for the repeated name-clash answer.</summary>
public sealed partial class StateStore
{
    /// <inheritdoc />
    public (ClashRefusal State, Issue? Existing) ClashRefusalState(WorkSelection selection, string path,
        IssueSubject subject, (ChangeNotice Notice, bool IsFolder, SyncedEntry? Before) work) =>
        Ordered(() => _changes.ClashRefusalState(selection, path, subject, work));
}
