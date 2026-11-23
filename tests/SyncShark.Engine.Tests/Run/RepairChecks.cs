using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>Contract checks for Repair.</summary>
public static class RepairChecks
{
    public static Task ManualRootRecheckAsync() => throw new NotImplementedException();
    public static Task OwedRootRecheckAsync() => throw new NotImplementedException();
    public static Task ChildRootControlAsync() => throw new NotImplementedException();

    public static Task BilateralNoticeAsync() => throw new NotImplementedException();
    public static Task HealthyDirtySetAsync() => throw new NotImplementedException();

    /// <summary>Check retained repair attempt at integration.</summary>
    public static Task RetainedAttemptAsync() => throw new NotImplementedException();
    /// <summary>Check covered repair hop at integration.</summary>
    public static Task CoveredHopAsync() => throw new NotImplementedException();
    public static Task CorruptHistoryAsync() => throw new NotImplementedException();
    /// <summary>Runs a repair check against a fresh store, pair and cancellation token.</summary>
    internal static Task WithRepairStore(Func<IStore,PairConfig,CancellationToken,Task> run,[CallerMemberName] string check="") => throw new NotImplementedException();
    /// <summary>Verifies the Repair contract through the module surface.</summary>
    public static Task RunAsync() => throw new NotImplementedException();
}
