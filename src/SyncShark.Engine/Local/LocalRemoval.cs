namespace Massdex.SyncShark.Engine.Local;

/// <summary>Advances durable pending ownership around classified recycle and permanent-removal effects.</summary>
internal readonly struct LocalRemoval(EngineSettings _settings, PendingNotes _notes)
{
    // The first and current pending encoding; Storage alone interprets it.
    private const int PendingEncodingVersion = 1;

    internal PendingEffect Arm(PendingKind kind, string path, RemovalSelection selection, Entry? source,
        StageProof? stage)
    {
        var armed = new PendingEffect(PendingEncodingVersion, ResourceNames.NewId(), 0, kind, PendingPhase.Armed, path,
            selection.Proof, selection.Saved, source, stage, null, null,
            _settings.WhenRecycleBinUnavailable == RecycleFallback.DeletePermanently, null);
        try { _notes.Arm(armed); }
        catch (InvalidOperationException error)
        {
            throw new ItemFailedException(path, TextKey.FailEarlierReplace, error);
        }
        return armed;
    }

    internal PendingEffect Advance(PendingEffect current, PendingEffect change)
    {
        var next = change with { Generation = current.Generation + 1 };
        _notes.Advance(current, next);
        return next;
    }

    // Records one classified effect under the armed note. A failed write leaves the earlier phase as the authority.
    private (PendingEffect Effect, RemovalResult Result) Record(PendingEffect current, RemovalResult result,
        string? binLocator, StageProof? victim)
    {
        var phase = result.Outcome switch
        {
            RemovalOutcome.Removed => PendingPhase.Removed,
            RemovalOutcome.Retained => PendingPhase.Retained,
            RemovalOutcome.Unknown => PendingPhase.Unknown,
            _ => current.Phase
        };
        var next = Advance(current, current with
        {
            Phase = phase, BinLocator = binLocator ?? current.BinLocator, Victim = victim, Cause = result.Cause
        });
        return (next, result with { OperationId = next.OperationId });
    }

    private static RemovalResult NoEffect(TextKey? cause) => new(RemovalOutcome.NoEffect, null, null, cause);

    // LocalBin throws only before it dispatches, and names a full path; the note's result carries the cause instead.
    internal async Task<(PendingEffect Effect, RemovalResult Result)> RemoveAsync(PendingEffect armed, string full,
        CancellationToken ct)
    {
        RemovalResult sent;
        string? binLocator = null;
        StageProof? victim = null;
        try { (sent, binLocator, victim) = await LocalRecycle.SendAsync(full, armed.Losing, ct).ConfigureAwait(false); }
        catch (ItemFailedException failed) { sent = NoEffect(failed.Cause); }
        catch (ChangedUnderneathException changed)
        {
            sent = RemovalResult.Refused(changed);
        }
        catch (OperationCanceledException) { sent = NoEffect(null); }

        // Current policy is rechecked so a revoked fallback wins over the permission frozen on the note.
        if (sent.Outcome != RemovalOutcome.NoEffect || sent.Cause != TextKey.RecycleUnavailable ||
            !armed.PermanentAllowed || _settings.WhenRecycleBinUnavailable != RecycleFallback.DeletePermanently)
            return Record(armed, sent, binLocator, victim);

        var permanent = Advance(armed, armed with { Phase = PendingPhase.PermanentArmed, Cause = sent.Cause });
        RemovalResult disposed;
        try { disposed = await LocalDisposition.DeletePermanentlyAsync(full, armed, ct).ConfigureAwait(false); }
        catch (ItemFailedException failed) { disposed = NoEffect(failed.Cause); }
        catch (ChangedUnderneathException changed)
        {
            disposed = RemovalResult.Refused(changed);
        }
        catch (OperationCanceledException) { disposed = NoEffect(null); }
        return Record(permanent, disposed, null, null);
    }

}
