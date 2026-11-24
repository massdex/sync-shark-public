using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Tests.Planner;

/// <summary>Checks whole-second client times separately from measured server deletions.</summary>
public static class TimeRulesChecks
{
    public static Task RunAsync()
    {
        var at = DateTimeOffset.UnixEpoch;
        var second = TimeSpan.FromSeconds(1);
        var future = at.AddDays(1);
        Check.Require(TimeRules.Compare(at, at.AddTicks(1), future) == TimeOrder.Equal, "Same UTC second");
        Check.Require(TimeRules.Compare(at, at.AddTicks(1), at) == TimeOrder.Unreliable, "Future tick in the same UTC second is unreliable");
        Check.Require(TimeRules.Compare(at, at + second, future) == TimeOrder.RightNewer, "Newer UTC second");
        Check.Require(TimeRules.Compare(at, future, at) == TimeOrder.Unreliable, "Future client stamp");
        var epoch = new DateTimeOffset(DateTime.FromFileTimeUtc(0));
        Check.Require(TimeRules.Compare(epoch, epoch, at) == TimeOrder.Unreliable, "Windows file-time epoch is an unset stamp");
        var firstSet = epoch.AddTicks(1);
        Check.Require(TimeRules.Compare(firstSet, firstSet, at) == TimeOrder.Equal, "First tick after the Windows file-time epoch is reliable");
        Check.Require(!TimeRules.DefinitelyDeletedLater(at, future, null), "Cannot delete without a measured clock");
        var clock = (Offset: second, Uncertainty: second);
        var boundary = at + clock.Offset + clock.Uncertainty;
        Check.Require(!TimeRules.DefinitelyDeletedLater(at, boundary, clock), "Deletion at uncertainty boundary");
        Check.Require(TimeRules.DefinitelyDeletedLater(at, boundary.AddTicks(1), clock), "Confirmed deletion beyond boundary");
        return Task.CompletedTask;
    }
}
