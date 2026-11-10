namespace Massdex.SyncShark.Engine.Local;

/// <summary>The Local recovery algorithm for the pair's single pending note (section 4.6). Only this type reads the
/// note's Losing, Stage, Victim, BinLocator and native phase; the caller settles from the returned result.</summary>
public sealed class PendingResolver
{
    private readonly PairConfig _pair;
    private readonly LocalListing _listing;
    private readonly Action<PendingEffect, PendingEffect> _advance;
    private readonly Func<PendingRead> _pending;

    /// <summary>Binds the pair's local view and pair-bound note store; a recovery call carries only its note.</summary>
    public PendingResolver(PairConfig pair, LocalListing listing,
        Action<PendingEffect, PendingEffect> advance, Func<PendingRead> pending)
    {
        _pair = pair;
        _listing = listing;
        _advance = advance;
        _pending = pending;
    }

    /// <summary>Applies the choice to the pending note. Callers read the result, never the state of the path.
    /// NoEffect without a cause means no custody remains and the note may be settled with no baseline; with a cause
    /// the read was refused and the note stands. A stale note throws with no effect, and the note's generation can
    /// advance here, so a caller settles against a fresh read.</summary>
    public async Task<RemovalResult> ResolveAsync(PendingRead pending, RecoveryChoice choice, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return pending switch
        {
            NoPending => new RemovalResult(RemovalOutcome.NoEffect, null, null, null),
            CurrentPending current => await ResolveCurrentAsync(current, choice, ct).ConfigureAwait(false),
            LegacyPending legacy => ResolveLegacy(legacy, choice),
            _ => throw new ArgumentOutOfRangeException(nameof(pending))
        };
    }

    private void RequireFresh(PendingRead pending, string path)
    {
        if (_pending() != pending) throw new ChangedUnderneathException(path);
    }

    // A legacy note has no proof, so nothing here publishes, adopts or deletes either locator.
    private RemovalResult ResolveLegacy(LegacyPending note, RecoveryChoice choice)
    {
        RequireFresh(note, note.Path);
        try
        {
            if (choice == RecoveryChoice.Check)
                return new RemovalResult(RemovalOutcome.Retained, note.RawToken, _listing.One(note.Path), null);
            if (!IsAbsent(note.StagePath, note.Path))
                return new RemovalResult(RemovalOutcome.Retained, note.RawToken, null, TextKey.FailChanged);
            var current = choice == RecoveryChoice.AcceptCurrent ? _listing.One(note.Path) : null;
            return new RemovalResult(RemovalOutcome.NoEffect, note.RawToken, current, null);
        }
        catch (ItemFailedException error)
        {
            return new RemovalResult(RemovalOutcome.Retained, note.RawToken, null, error.Cause);
        }
    }

    private async Task<RemovalResult> ResolveCurrentAsync(CurrentPending pending, RecoveryChoice choice,
        CancellationToken ct)
    {
        var note = pending.Value;
        RequireFresh(pending, note.Path);
        try
        {
            return choice == RecoveryChoice.Check
                ? await CheckAsync(note, ct).ConfigureAwait(false)
                : await AcknowledgeAsync(note, choice, ct).ConfigureAwait(false);
        }
        catch (ItemFailedException error) when (_pending() is CurrentPending { Value: var latest })
        {
            // The cause must survive a restart; a refused read changes neither phase nor authority.
            if (latest.Cause != error.Cause) latest = Advance(latest, latest.Phase, error.Cause);
            return new RemovalResult(RemovalOutcome.NoEffect, latest.OperationId, null, error.Cause);
        }
    }

    private async Task<RemovalResult> CheckAsync(PendingEffect note, CancellationToken ct)
    {
        if (note.Phase == PendingPhase.Recycled)
        {
            if (note.BinLocator is not { } receipt ||
                !await MatchesAsync(receipt, LosingProof(note, receipt), note.Path, ct).ConfigureAwait(false))
                return Held(RemovalOutcome.Unknown, note);
            note = Advance(note, PendingPhase.Removed, note.Cause);
        }
        return note.Phase switch
        {
            PendingPhase.Removed when note.Kind == PendingKind.LocalDelete =>
                new RemovalResult(RemovalOutcome.Removed, note.OperationId, null, null),
            PendingPhase.Removed or PendingPhase.PublishArmed or PendingPhase.Published =>
                await PublishAsync(note, ct).ConfigureAwait(false),
            PendingPhase.RestoreArmed or PendingPhase.WrongRestored =>
                await RestoredAsync(note, ct).ConfigureAwait(false),
            PendingPhase.DiscardArmed =>
                await AcknowledgeAsync(note, RecoveryChoice.AbandonIncoming, ct).ConfigureAwait(false),
            PendingPhase.Retained => Held(RemovalOutcome.Retained, note),
            // Armed, PermanentArmed and Unknown cannot tell a crash before the effect from one after it.
            _ => Held(RemovalOutcome.Unknown, note)
        };
    }

    // The target can already hold the stage's own object after a crash; any other occupant is a collision.
    private async Task<RemovalResult> PublishAsync(PendingEffect note, CancellationToken ct)
    {
        if (note.Stage is not { } stage) return Blocked(note);
        var final = await VerifiedFinalAsync(note.Path, stage, ct).ConfigureAwait(false);
        if (final is null)
        {
            if (note.Phase == PendingPhase.Published ||
                !await MatchesAsync(stage.Path, stage, note.Path, ct).ConfigureAwait(false) ||
                _listing.One(note.Path) is not null)
                return Blocked(note);
            if (note.Phase == PendingPhase.Removed) note = Advance(note, PendingPhase.PublishArmed, note.Cause);
            MoveToFinal(note.Path, stage.Path);
            final = await VerifiedFinalAsync(note.Path, stage, ct).ConfigureAwait(false);
            if (final is null) return Blocked(note);
        }
        if (note.Phase != PendingPhase.Published) note = Advance(note, PendingPhase.Published, note.Cause);
        return new RemovalResult(RemovalOutcome.Published, note.OperationId, final, null);
    }

    private void MoveToFinal(string path, string stagePath)
    {
        try
        {
            LocalSide.PreparePublication(stagePath);
            File.Move(stagePath, LocalPaths.Full(_pair.LocalRoot, path));
        }
        catch (Exception error) when (LocalErrors.IsFileFailure(error))
        {
            throw new ItemFailedException(path, LocalErrors.Cause(error, TextKey.PublishFailed), error);
        }
    }

    // A restore send is uncertain until the original path proves the victim, and it is never repeated.
    private async Task<RemovalResult> RestoredAsync(PendingEffect note, CancellationToken ct)
    {
        var restored = note.Victim is { } victim
            ? await VerifiedFinalAsync(note.Path, victim, ct).ConfigureAwait(false)
            : null;
        if (note.Phase == PendingPhase.RestoreArmed)
        {
            if (restored is null) return Held(RemovalOutcome.Unknown, note);
            note = Advance(note, PendingPhase.WrongRestored, note.Cause);
        }
        return new RemovalResult(RemovalOutcome.WrongRestored, note.OperationId, restored, null);
    }

    // Both terminal choices drop only a still-owned stage, so settlement cannot strand it; no bin item or occupant is
    // touched and no history is claimed.
    private async Task<RemovalResult> AcknowledgeAsync(PendingEffect note, RecoveryChoice choice, CancellationToken ct)
    {
        var current = choice == RecoveryChoice.AcceptCurrent ? _listing.One(note.Path) : null;
        if (choice == RecoveryChoice.AcceptCurrent && current is null) return Blocked(note);
        if (note.Stage is { } stage && !await DiscardAsync(note, stage, ct).ConfigureAwait(false)) return Blocked(note);
        return new RemovalResult(RemovalOutcome.NoEffect, note.OperationId, current, null);
    }

    // A missing stage only reconciles an armed disposal; it never proves a removal or publication.
    private async Task<bool> DiscardAsync(PendingEffect note, StageProof stage, CancellationToken ct)
    {
        if (IsAbsent(stage.Path, note.Path)) return true;
        try
        {
            using var held = SyncLocalFileAuthority.OpenExistingExclusive(stage.Path);
            if (!await LocalFileProof.HoldsAsync(held, stage, ct).ConfigureAwait(false)) return false;
            if (note.Phase != PendingPhase.DiscardArmed) Advance(note, PendingPhase.DiscardArmed, note.Cause);
            held.DeleteOnClose();
            return true;
        }
        catch (Exception error) when (LocalErrors.IsFileFailure(error))
        {
            throw ReadFailure(note.Path, error);
        }
    }

    private PendingEffect Advance(PendingEffect note, PendingPhase phase, TextKey? cause)
    {
        var next = note with { Generation = note.Generation + 1, Phase = phase, Cause = cause };
        _advance(note, next);
        return next;
    }

    private static RemovalResult Held(RemovalOutcome outcome, PendingEffect note) =>
        new(outcome, note.OperationId, null, note.Cause);

    private static RemovalResult Blocked(PendingEffect note) =>
        new(RemovalOutcome.Retained, note.OperationId, null, note.Cause ?? TextKey.FailChanged);

    private static StageProof LosingProof(PendingEffect note, string receipt) =>
        new(receipt, note.Losing.VolumeId, note.Losing.Expected.Id, note.Losing.Expected.Size, note.Losing.ContentHash);

    private async Task<Entry?> VerifiedFinalAsync(string path, StageProof proof, CancellationToken ct)
    {
        var entry = _listing.One(path);
        return entry is { IsFolder: false, Skip: null } && entry.Size == proof.Size &&
            await MatchesAsync(LocalPaths.Full(_pair.LocalRoot, path), proof, path, ct).ConfigureAwait(false)
            ? entry : null;
    }

    private static async Task<bool> MatchesAsync(string fullPath, StageProof proof, string subject,
        CancellationToken ct)
    {
        if (IsAbsent(fullPath, subject)) return false;
        try
        {
            using var held = SyncLocalFileAuthority.OpenExistingSharedRead(fullPath);
            return await LocalFileProof.HoldsAsync(held, proof, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (LocalErrors.IsFileFailure(error))
        {
            throw ReadFailure(subject, error);
        }
    }

    // GetAttributes does not follow a link, so a dangling or substituted locator still counts as present.
    private static bool IsAbsent(string fullPath, string subject)
    {
        try
        {
            File.GetAttributes(fullPath);
            return false;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception error) when (LocalErrors.IsFileFailure(error))
        {
            throw ReadFailure(subject, error);
        }
    }

    private static ItemFailedException ReadFailure(string path, Exception error) =>
        new(path, LocalErrors.Cause(error, TextKey.ReadLocalFailed), error);
}
