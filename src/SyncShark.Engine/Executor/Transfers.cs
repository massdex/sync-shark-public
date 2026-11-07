namespace Massdex.SyncShark.Engine.Executor;

/// <summary>The shared upload and download steps; they take no permit (section 7.1).</summary>
public sealed class Transfers(ExecutorContext context)
{
    private readonly ExecutorContext context = context;
    private readonly Dictionary<Side, long> _noSpace = [];

    /// <summary>Stages the local file's content and publishes it on Dropbox, requiring expected's version when given;
    /// staged reports the one upload-stage completion.</summary>
    public async Task<Entry> UpAsync(Entry local, string target, Entry? expected, CancellationToken ct,
        Func<ValueTask>? staged = null)
    {
        var result = await CopyAsync(Side.Dropbox, local, target, expected, null, staged, null, ct)
            .ConfigureAwait(false);
        return result.Final ?? throw new ChangedUnderneathException(target);
    }

    /// <summary>A replace holds the pair's replace turn around publish and commit (7.3); commit receives the
    /// classified result inside that turn and runs only on Published.</summary>
    public Task<RemovalResult> DownAsync(Entry dropbox, string target, Entry? expected, RemovalSelection? selection,
        Action<RemovalResult> commit, CancellationToken ct) =>
        CopyAsync(Side.Local, dropbox, target, expected, selection, null, commit, ct);

    /// <summary>Holds the local pending-note turn through removal and its settlement.</summary>
    public async Task<RemovalResult> RemoveAsync(Side side, Entry entry, RemovalSelection? selection,
        Action<RemovalResult> settle, CancellationToken ct, bool ownedFolderEffect = false)
    {
        ct.ThrowIfCancellationRequested();
        await using var turn = side == Side.Local ? await context.Runtime.EnterAsync(ct).ConfigureAwait(false) : null;
        // A frozen folder plan already owns its removal; ordinary live deletion acquires no such authority.
        ItemSync.RequireLiveWrite(context, side == Side.Local && !ownedFolderEffect);
        var result = await context.For(side).DeleteAsync(entry, selection, ct).ConfigureAwait(false);
        settle(result);
        return result;
    }

    private async Task<RemovalResult> CopyAsync(Side destination, Entry source, string target, Entry? expected,
        RemovalSelection? selection, Func<ValueTask>? staged, Action<RemovalResult>? commit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Only a file replacement owns the pending slot and replace turn; a folder replacement recycles the folder
        // after its stage is closed and verified instead (P1).
        var replacingLocal = destination == Side.Local && expected is { IsFolder: false };
        if (replacingLocal && !context.Recovery.CanArmReplace())
            throw new ItemFailedException(target, TextKey.FailEarlierReplace, null);
        var cause = destination == Side.Local ? TextKey.FailLocalNoSpace : TextKey.FailDropboxNoSpace;
        lock (_noSpace)
        {
            if (_noSpace.TryGetValue(destination, out var size) && source.Size >= size)
                throw new ItemFailedException(target, cause, null);
        }

        var (written, raw, content) = await StageAsync(destination, source, target, ct).ConfigureAwait(false);
        var sourceFailed = content.Failure is not null;
        await using (written)
        {
            // A session stage is complete before publication; a direct stage borrows the source until its result.
            if (!written.ConsumesSourceOnPublish) await HandOffAsync(raw, content, staged).ConfigureAwait(false);
            RemovalResult result;
            try
            {
                await using var turn = replacingLocal ? await context.Runtime.EnterAsync(ct).ConfigureAwait(false) : null;
                result = await WriteAsync(destination, written, expected, selection, source.Size, sourceFailed, ct)
                    .ConfigureAwait(false);
                if (result.Outcome == RemovalOutcome.Published) commit?.Invoke(result);
                else if (result.Outcome == RemovalOutcome.NoEffect && result.OperationId is not null)
                    SettleNoEffect(result.OperationId);
            }
            finally
            {
                if (written.ConsumesSourceOnPublish) await CloseAsync(raw, content).ConfigureAwait(false);
            }
            if (written.ConsumesSourceOnPublish && staged is not null) await staged().ConfigureAwait(false);
            return result;
        }
    }

    /// <summary>Publishes through the destination owner and records a Dropbox space refusal.</summary>
    private async Task<RemovalResult> WriteAsync(Side destination, IStaged written, Entry? expected,
        RemovalSelection? selection, long size, bool sourceFailed, CancellationToken ct)
    {
        try
        {
            return await context.For(destination).PublishAsync(written, expected, selection, ct).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (destination == Side.Dropbox) RecordFailure(size, sourceFailed, error);
            throw;
        }
    }

    /// <summary>Opens the source once and stages it. A Local download may retry once on a source failure through the
    /// shared attempt owner; the returned source views stay open only while the stage borrows the source.</summary>
    private async Task<(IStaged Staged, Stream Raw, SourceStream Content)> StageAsync(
        Side destination, Entry source, string target, CancellationToken ct)
    {
        var reading = context.For(PathNames.Other(destination));
        var writing = context.For(destination);
        var retried = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            RequestAttempt? attempt = null;
            Stream? raw = null;
            SourceStream? content = null;
            IAsyncDisposable? lease = null;
            IStaged? written = null;
            try
            {
                if (destination == Side.Local)
                {
                    lease = await ReserveAsync(target, source.Size, ct).ConfigureAwait(false);
                    attempt = context.BeginDownloadAttempt(source);
                }
                raw = await reading.OpenReadAsync(source, attempt, ct).ConfigureAwait(false);
                content = new SourceStream(raw);
                written = await writing.StageAsync(target, content, source, ct).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                var sourceFailed = content?.Failure is not null;
                await CloseAsync(raw, content).ConfigureAwait(false);
                // The attempt owner settles every source request, including the one that ends the retry.
                var retry = attempt is not null &&
                    await context.CompleteDownloadAttemptAsync(attempt, ct).ConfigureAwait(false);
                if (destination != Side.Local || retried || attempt is null || !retry)
                {
                    if (destination == Side.Dropbox) RecordFailure(source.Size, sourceFailed, error);
                    throw;
                }
                retried = true;
                continue;
            }
            finally
            {
                // The lease covers the source request and its stage only; it is never held through publication.
                if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false);
            }
            // A validated stage is the download's success boundary; an upload has no attempt to claim.
            if (attempt is not null) await context.CompleteDownloadAttemptAsync(attempt, ct).ConfigureAwait(false);
            return (written!, raw!, content!);
        }
    }

    /// <summary>Transfers owns acquiring the download reservation for its stage; the accounting owner supplies it.</summary>
    private ValueTask<IAsyncDisposable> ReserveAsync(string target, long size, CancellationToken ct) =>
        context.ReserveDownloadAsync(context.Pair.LocalRoot, target, size, ct);

    /// <summary>Closes both source views and reports the one upload-stage completion once.</summary>
    private static async Task HandOffAsync(Stream? raw, Stream? content, Func<ValueTask>? staged)
    {
        await CloseAsync(raw, content).ConfigureAwait(false);
        if (staged is not null) await staged().ConfigureAwait(false);
    }

    private static async Task CloseAsync(Stream? raw, Stream? content)
    {
        if (content is not null) await content.DisposeAsync().ConfigureAwait(false);
        if (raw is not null) await raw.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>A definite removal refusal leaves its armed note behind; settling it here frees the slot before the
    /// unowned stage is disposed.</summary>
    private void SettleNoEffect(string operationId)
    {
        if (context.Store.Pending(context.Pair.PairId) is CurrentPending pending &&
            pending.Value.OperationId == operationId)
            context.Store.SettlePending(context.Pair.PairId, pending.Value, StateChange.Empty);
    }

    /// <summary>The Dropbox space refusal latches the smallest refused size, so any source at or above it is refused
    /// too; a source read failure proves nothing about destination space.</summary>
    private void RecordFailure(long size, bool sourceFailed, Exception error)
    {
        if (sourceFailed) return;
        if (error is not ItemFailedException failed || failed.Cause != TextKey.FailDropboxNoSpace) return;
        lock (_noSpace)
        {
            _noSpace[Side.Dropbox] = _noSpace.TryGetValue(Side.Dropbox, out var previous)
                ? Math.Min(previous, size) : size;
        }
    }

    // The transfer owns the underlying stream; this view only identifies failures from consuming it.
    private sealed class SourceStream(Stream source) : Stream
    {
        public Exception? Failure { get; private set; }
        public override bool CanRead => source.CanRead;
        public override bool CanSeek => source.CanSeek;
        public override bool CanWrite => false;
        public override long Length => source.Length;
        public override long Position { get => source.Position; set => source.Position = value; }
        public override void Flush() => source.Flush();
        public override long Seek(long offset, SeekOrigin origin) => source.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            try { return source.Read(buffer, offset, count); }
            catch (Exception error)
            {
                Failure = error;
                throw;
            }
        }

        public override int Read(Span<byte> buffer)
        {
            try { return source.Read(buffer); }
            catch (Exception error)
            {
                Failure = error;
                throw;
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            try { return await source.ReadAsync(buffer, ct).ConfigureAwait(false); }
            catch (Exception error)
            {
                Failure = error;
                throw;
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            ValidateBufferArguments(buffer, offset, count);
            return ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        }
    }
}
