using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Retains a branch's observed identity and maps its unfinished effects back to exact residual work.</summary>
internal sealed class SelectionBranch(SelectionPass pass, Parents parents,
    WorkSelection selection, PendingChange work, ChangeNotice? original, string key, SelectionPeer? peer,
    long? throughPeer, Func<Side, string, string?, bool>? separateWork, (string Path, Entry? Entry)? primary)
{
    internal WorkSelection Selection => selection;
    internal ChangeNotice Notice => work.Notice;
    internal ChangeNotice? Original => original;
    internal string Key => key;
    internal SyncedEntry? Saved => work.Before;
    internal SyncedEntry? Before { get; private set; } = work.Before;
    internal SelectionPeer? Peer { get; private set; } = peer;
    internal long? ThroughPeer => throughPeer;
    internal Func<Side, string, string?, bool>? SeparateWork => separateWork;
    internal LocalEvidence.DirtyProof? Evidence { get; set; }
    internal bool Attached { get; private set; }
    internal bool Failed { get; set; }

    internal (string Path, Entry? Entry)? TakePrimary()
    {
        var supplied = primary;
        primary = null;
        return supplied;
    }

    internal void ClaimPeer(Row raw, string? otherKey)
    {
        if (throughPeer is { } through)
        {
            Peer = new SelectionPeer(otherKey!, through);
            pass.ClaimPeer(selection, Peer);
        }
        Before = work.Before;
    }

    internal void RefreshBefore(Row raw, string? otherKey) => Before = raw.Synced;

    internal void Retain(Row effective, Issue issue)
    {
        var notice = work.Notice;
        var own = effective.Key == key || notice.Id is not null && effective.EntryOn(notice.Side)?.Id == notice.Id;
        var residual = own
            ? original is not null && original.Path != notice.Path ? original :
                notice with { Path = issue.Path, Observed = effective.EntryOn(notice.Side) }
            : SelectionBranches.ChildNotice(selection.Side, effective.PathOn(selection.Side) ??
                parents.Counterpart(selection.Side, effective.PathOn(PathNames.Other(selection.Side)) ?? issue.Path,
                    effective.Synced), null);
        Attached |= pass.RetainIssue(selection, effective, residual, own ? Before : effective.Synced, issue,
            retained => { if (own) pass.RememberHeld(original, key, effective, Before, retained); });
    }
}
