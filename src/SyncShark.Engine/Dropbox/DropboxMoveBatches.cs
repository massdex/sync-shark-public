namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Sends concurrent plain file moves through move_batch_v2: many parallel single moves into one namespace draw
/// too_many_write_operations, and Dropbox's remedy is batching. Moves are grouped by source and destination parent,
/// whichever pass sent them. A group sends what is queued whenever none of its batches is in flight, so a lone move
/// goes as a batch of one and there is no fill wait. Each caller awaits its own member, so a queue holds at most the
/// moves its callers' admission lets run at once; a batch holds at most the provider bound. A cancelled caller whose
/// member is still queued leaves the queue; once taken into a batch the member waits until that request is accounted
/// for, so its caller keeps its locks while the move can still happen, and is cancelled only when no effect is
/// proven. The request itself stops once every member's caller has stopped.</summary>
internal sealed class DropboxMoveBatches(DropboxWriter writer, PairConfig pair, EngineSettings settings, TimeProvider time)
{
    // A key is present exactly while its group drains; its queue holds the members waiting for the next batch.
    private readonly Dictionary<(string From, string To), LinkedList<Pending>> _groups = new();

    private sealed class Pending(Entry expected, string newPath, CancellationToken token)
    {
        internal Entry Expected { get; } = expected;
        internal string NewPath { get; } = newPath;
        internal CancellationToken Token { get; } = token;
        internal TaskCompletionSource<Entry> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal LinkedListNode<Pending>? Node { get; set; }
        internal CancellationTokenRegistration Registration { get; set; }
        internal IssueSubject.UnknownMove Move => new(Expected.Id, Expected.Path, NewPath, null, Expected.IsFolder);
    }

    /// <summary>Moves expected to newPath, which must be free, with the outcome the single move gives.</summary>
    internal Task<Entry> MoveAsync(Entry expected, string newPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var member = new Pending(expected, newPath, ct);
        var key = (PathNames.Key(PathNames.Parent(expected.Path)), PathNames.Key(PathNames.Parent(newPath)));
        bool start;
        lock (_groups)
        {
            start = !_groups.TryGetValue(key, out var queue);
            if (start) _groups[key] = queue = new LinkedList<Pending>();
            member.Node = queue!.AddLast(member);
            member.Registration = ct.Register(Cancel, member);
        }
        if (start) _ = DrainAsync(key);
        return member.Done.Task;
    }

    // Only a member still queued leaves on cancellation; a member in a batch waits for that request's accounting.
    private void Cancel(object? state)
    {
        var member = (Pending)state!;
        var removed = false;
        lock (_groups)
        {
            if (member.Node?.List is { } queue)
            {
                queue.Remove(member.Node);
                removed = true;
            }
        }
        if (removed) member.Done.TrySetCanceled(member.Token);
    }

    // One drain per group; SendAsync settles every member it takes, so this task never faults.
    private async Task DrainAsync((string From, string To) key)
    {
        while (true)
        {
            Pending[] batch;
            lock (_groups)
            {
                var queue = _groups[key];
                if (queue.Count == 0)
                {
                    _groups.Remove(key);
                    return;
                }
                batch = new Pending[Math.Min(queue.Count, DropboxApiLimits.BatchMaxEntries)];
                for (var index = 0; index < batch.Length; index++)
                {
                    batch[index] = queue.First!.Value;
                    queue.RemoveFirst();
                }
            }
            await SendAsync(key, batch).ConfigureAwait(false);
        }
    }

    private async Task SendAsync((string From, string To) key, Pending[] batch)
    {
        if (await SubmitWhileLiveAsync(batch).ConfigureAwait(false) is not { } results) return;
        for (var ordinal = 0; ordinal < batch.Length; ordinal++)
        {
            var member = batch[ordinal];
            switch (results[ordinal])
            {
                case FolderMemberResult.Receipt receipt:
                    member.Registration.Dispose();
                    member.Done.TrySetResult(DropboxListing.Respelled(receipt.Entry, member.NewPath));
                    break;
                case FolderMemberResult.KnownNoEffect { WriteThrottled: true } throttled:
                    // Not committed; the writer reported the throttle, and a live member goes in the group's next batch.
                    // The cancellation check shares the queue lock with Cancel, so a cancelled member is never re-sent.
                    var requeued = false;
                    lock (_groups)
                    {
                        if (!member.Token.IsCancellationRequested)
                        {
                            member.Node = _groups[key].AddLast(member);
                            requeued = true;
                        }
                    }
                    if (!requeued) Fail(member, Failure(member, throttled));
                    break;
                case var result:
                    Fail(member, Failure(member, result));
                    break;
            }
        }
    }

    // The request runs while any member's caller is live; once all have stopped it stops like a single move's retry.
    private async Task<IReadOnlyList<FolderMemberResult>?> SubmitWhileLiveAsync(Pending[] batch)
    {
        using var stop = new CancellationTokenSource();
        var live = batch.Length;
        var registrations = batch.Select(member => member.Token.Register(() =>
        {
            if (Interlocked.Decrement(ref live) == 0) stop.Cancel();
        })).ToArray();
        try { return await SubmitAsync(batch, stop.Token).ConfigureAwait(false); }
        finally { foreach (var registration in registrations) registration.Dispose(); }
    }

    // A cancelled caller is cancelled only when its move proved no effect; an unknown effect keeps its intended move.
    private static void Fail(Pending member, Exception error)
    {
        member.Registration.Dispose();
        if (member.Token.IsCancellationRequested && error is not ItemFailedException { Move: not null })
            member.Done.TrySetCanceled(member.Token);
        else member.Done.TrySetException(error);
    }

    // The batch's member results, or null when the request's own outcome already settled every member.
    private async Task<IReadOnlyList<FolderMemberResult>?> SubmitAsync(Pending[] batch, CancellationToken ct)
    {
        var attempt = new RequestAttempt(new object(), AttemptOwner.Request);
        var accepted = false;
        try
        {
            // Sources are paths, not identities: a source moved meanwhile is refused as the single move refuses it.
            switch (await writer.SubmitMoveBatchAsync(batch.Select(member =>
                (DropboxPaths.Remote(pair, member.Expected.Path), member.NewPath)).ToArray(), attempt, ct).ConfigureAwait(false))
            {
                case FolderSubmitResult.Terminal terminal:
                    return terminal.Results;
                case FolderSubmitResult.Accepted job:
                    accepted = true;
                    return await JoinAsync(job.JobId, batch.Length, ct).ConfigureAwait(false);
                case FolderSubmitResult.KnownNoEffect refused:
                    foreach (var member in batch) Fail(member, NotSent(member, refused.Cause, attempt));
                    return null;
                default:
                    foreach (var member in batch) Fail(member, Unknown(member));
                    return null;
            }
        }
        catch (Exception error)
        {
            foreach (var member in batch) Fail(member, RequestFailure(member, error, attempt, accepted, ct));
            return null;
        }
    }

    private async Task<IReadOnlyList<FolderMemberResult>> JoinAsync(string jobId, int count, CancellationToken ct)
    {
        while (true)
        {
            var result = await writer.ReadMoveBatchAsync(jobId, count,
                new RequestAttempt(new object(), AttemptOwner.Request), ct).ConfigureAwait(false);
            if (result is FolderJobResult.Terminal terminal) return terminal.Results;
            await Task.Delay(settings.FolderDeletePollInterval, time, ct).ConfigureAwait(false);
        }
    }

    // One member's single-move outcome: name taken, a changed source, a documented refusal or an unknown effect.
    private static Exception Failure(Pending member, FolderMemberResult result) => result switch
    {
        FolderMemberResult.KnownNoEffect { Cause: TextKey.ClashNameTaken } => new PathOccupiedException(member.NewPath),
        FolderMemberResult.KnownNoEffect { Cause: TextKey.FailChanged } => new ChangedUnderneathException(member.NewPath),
        FolderMemberResult.KnownNoEffect known => new ItemFailedException(member.NewPath, known.Cause ?? TextKey.DropboxRefused, null),
        _ => Unknown(member)
    };

    // The single move's unknown-effect failure, keeping the intended move for settlement by identity.
    private static Exception Unknown(Pending member) => DropboxErrors.WithMove(
        new ItemFailedException(member.NewPath, TextKey.DropboxRefused, null) { Disposition = FailureDisposition.UnknownEffect },
        member.Move);

    // The request proved it had no effect without a usable reply: the single move's temporary refusal.
    private static ItemFailedException NotSent(Pending member, TextKey? cause, RequestAttempt attempt) =>
        new(member.NewPath, cause ?? TextKey.DropboxRefused, null)
        {
            Disposition = FailureDisposition.TemporaryNoEffect, ProviderWait = attempt.Completion?.ProviderWait,
            ProviderWaitStarted = attempt.ProviderWaitStartedTimestamp
        };

    // A failed request maps as the single move's would; once the job was accepted its effect is unknown.
    private static Exception RequestFailure(Pending member, Exception error, RequestAttempt attempt, bool accepted,
        CancellationToken ct)
    {
        // Every caller stopped: proven-no-effect members are cancelled, an accepted or uncertain write stays unknown.
        if (error is OperationCanceledException && ct.IsCancellationRequested)
            return accepted || attempt.Completion is not { NoEffectProven: true } ? Unknown(member) : error;
        if (accepted) return Unknown(member);
        var mapped = error is HttpRequestException http
            ? DropboxErrors.Map(DropboxProtocol.MoveBatch, http, false, member.NewPath, attempt).Error
            : DropboxErrors.NoUsableReply(error, attempt, ct)
                ? DropboxErrors.Refused(member.NewPath, error, DropboxProtocol.MoveBatch, attempt) : error;
        return DropboxErrors.WithMove(mapped, member.Move);
    }
}
