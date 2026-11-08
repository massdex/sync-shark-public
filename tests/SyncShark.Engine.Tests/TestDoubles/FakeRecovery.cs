namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

/// <summary>An ILocalRecovery for Executor and Run checks, with settable drive evidence and cleanup failures, a scripted pending result and recorded calls; the recovery-stage members are unused and throw.</summary>
public sealed class FakeRecovery : ILocalRecovery
{
    private int removals;

    public Task<RemovalResult> ResolvePendingAsync(PendingRead pending, RecoveryChoice choice, CancellationToken ct)
    {
        Resolved.Add((pending, choice));
        return Task.FromResult(Result);
    }

    public IReadOnlyList<(string Path, Exception Error)> RemoveStaleStages(IReadOnlySet<string> pendingStageNames)
    {
        removals++;
        return CleanupFailures;
    }

    public DriveEvidence Drive() => Evidence;
    public Func<PendingRead> Pending { get; set; } = static () => new NoPending();
    public bool CanArmReplace() => Pending() is NoPending;

    public Func<Entry, CancellationToken, Task<string>>? Hash { get; set; }

    public async Task<RemovalProof> ProveRemovalAsync(Entry expected, string? hash, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        hash ??= Hash is { } read ? await read(expected, ct) : expected.ContentHash;
        if (string.IsNullOrWhiteSpace(hash) || Evidence.Serial is not { } serial)
            throw new ChangedUnderneathException(expected.Path);
        return new RemovalProof(expected, serial.ToString(System.Globalization.CultureInfo.InvariantCulture), hash);
    }

    public StageAllocation PlanRecoveryStage(string operationId, long ordinal) => throw new NotSupportedException();

    public Task<IRecoveryStage> StageRecoveryAsync(StageAllocation allocation, Entry selected, Stream revisionContent,
        CancellationToken ct) => throw new NotSupportedException();

    public Task<ILocalRecovery.AllocationOccupancy> InspectRecoveryAllocationAsync(StageAllocation allocation,
        CancellationToken ct) => throw new NotSupportedException();

    public Task<Stream> OpenRecoveryStageAsync(StageProof proof, CancellationToken ct) => throw new NotSupportedException();

    public Task DiscardRecoveryStageAsync(StageProof proof, CancellationToken ct) => throw new NotSupportedException();

    /// <summary>What Drive() returns (all null = unavailable).</summary>
    public DriveEvidence Evidence { get; set; } = new(null, null, null);

    /// <summary>The operational cleanup failures RemoveStaleStages reports.</summary>
    public IReadOnlyList<(string Path, Exception Error)> CleanupFailures { get; set; } = [];

    /// <summary>What ResolvePendingAsync returns; the default claims no effect, so nothing settles as success unscripted.</summary>
    public RemovalResult Result { get; set; } = new(RemovalOutcome.NoEffect, null, null, null);

    /// <summary>The pending reads and choices ResolvePendingAsync was called with, in order.</summary>
    public List<(PendingRead Pending, RecoveryChoice Choice)> Resolved { get; } = new();

    /// <summary>How many times RemoveStaleStages was called.</summary>
    public int Removals => removals;
}
