namespace Massdex.SyncShark.Engine.Executor;

/// <summary>The checked preparation outcome before operation execution.</summary>
internal enum FolderRemovalCheck
{
    Passed, Waiting, Absent, Replaced, NotJustified, Declined, ForeignOperation, Mount, NotAccepted
}
