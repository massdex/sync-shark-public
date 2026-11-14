using Massdex.SyncShark.Engine.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

internal static class PathReadBarrierChecks
{
    // Reuse EngineHostChecks' ReceiptHandler.FirstRead signal bound for these held-provider fixtures.
    internal static readonly TimeSpan SignalWait = TimeSpan.FromSeconds(30);

    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        ProducerCancellation();
        await JoinedReadOutcomesAsync();
        var failures = new List<Exception>();
        foreach (var outcome in new[] { "value", "item", "stop", "terminal", "cancel-item", "cancel-aggregate", "cancel-foreign" })
        {
            try { await OverlapAsync(outcome); }
            catch (Exception error) { failures.Add(new InvalidOperationException(outcome, error)); }
        }
        if (failures.Count != 0) throw new AggregateException(failures);
        await IndependentPathsAsync();
        await WaitingCancellationAsync();
        var failure = new OperationCanceledException();
        var reads = 0;
        var barrier = new PathReadBarrier((_, _) => { reads++; return Task.FromException<Entry?>(failure); });
        Check.Require(ReferenceEquals(await FailureAsync(barrier.ReadAsync("a/x", default)), failure) && reads == 1,
            "A lone terminal cancellation is returned unchanged without a replacement read.");
    }

    private static void ProducerCancellation()
    {
        using var producer = new CancellationTokenSource();
        using var foreign = new CancellationTokenSource();
        foreign.Cancel();
        Check.Require(!PathReadBarrier.IsProducerCancellation(new OperationCanceledException(), producer.Token) &&
            !PathReadBarrier.IsProducerCancellation(new OperationCanceledException(producer.Token), producer.Token),
            "A terminal cancellation with a live producer is not its stop.");
        producer.Cancel();
        Check.Require(PathReadBarrier.IsProducerCancellation(new OperationCanceledException(producer.Token), producer.Token),
            "A cancellation carrying the cancelled producer's own token is its stop.");
        var misclassified = new Exception?[] { null, new ItemFailedException("a/x", TextKey.ReadDropboxFailed, null),
            new AggregateException(new IOException("terminal")), new OperationCanceledException(foreign.Token) }
            .Where(failure => PathReadBarrier.IsProducerCancellation(failure, producer.Token))
            .Select(failure => failure?.GetType().Name ?? "success").ToArray();
        Check.Require(misclassified.Length == 0,
            "A cancelled producer must not reclassify terminal outcomes: " + string.Join(", ", misclassified));
    }

    private static async Task JoinedReadOutcomesAsync()
    {
        var value = new Entry("a/x", false, 1, DateTimeOffset.UnixEpoch, "id", "r1", "hash", null);
        foreach (var result in new[] { value, null })
        {
            var read = new PathReadBarrier.Read(1);
            read.Done.SetResult(result);
            var observed = await PathReadBarrier.AwaitReadAsync(read, true, default);
            Check.Require(!observed.Again && ReferenceEquals(observed.Value, result),
                "A completed joined success or absence is delivered, not retried.");
        }
        using var producer = new CancellationTokenSource();
        using var foreign = new CancellationTokenSource();
        producer.Cancel();
        foreign.Cancel();
        foreach (var failure in new Exception[] { new ItemFailedException("a/x", TextKey.ReadDropboxFailed, null),
            new AggregateException(new IOException("terminal")), new OperationCanceledException(),
            new OperationCanceledException(foreign.Token), new OperationCanceledException(producer.Token) })
        foreach (var callerCancelled in new[] { false, true })
        {
            using var caller = new CancellationTokenSource();
            if (callerCancelled) caller.Cancel();
            var stopped = failure is OperationCanceledException cancellation && cancellation.CancellationToken == producer.Token;
            var read = new PathReadBarrier.Read(1) { ProducerCancelled = stopped };
            read.Done.SetException(failure);
            Exception? observed = null;
            (bool Again, Entry? Value)? result = null;
            try { result = await PathReadBarrier.AwaitReadAsync(read, true, caller.Token); }
            catch (Exception error) { observed = error; }
            Check.Require(stopped && !callerCancelled ? result is { Again: true, Value: null } && observed is null :
                result is null && ReferenceEquals(observed, failure),
                "A joined terminal failure stays unchanged; only a live follower of a stopped producer retries: " +
                failure.GetType().Name + "/" + stopped + "/" + callerCancelled);
        }
        var older = new PathReadBarrier.Read(1);
        older.Done.SetException(new ItemFailedException("a/x", TextKey.ReadDropboxFailed, null));
        var held = await PathReadBarrier.AwaitReadAsync(older, answer: false, ct: foreign.Token);
        Check.Require(held is { Again: true, Value: null },
            "An older failure returns a cancelled held-back caller to its own cancellation check.");
    }

    private static async Task OverlapAsync(string outcome)
    {
        using var first = new CancellationTokenSource();
        using var left = new CancellationTokenSource();
        using var right = new CancellationTokenSource();
        using var foreign = new CancellationTokenSource();
        foreign.Cancel();
        var gates = Enumerable.Range(0, 2).Select(_ => new TaskCompletionSource<Entry?>(
            TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var arrived = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var values = Enumerable.Range(1, 4).Select(version => new Entry("a/x", false, 1,
            DateTimeOffset.UnixEpoch, "id", "r" + version, "hash", null)).ToArray();
        var reads = 0;
        var active = 0;
        var barrier = new PathReadBarrier(async (_, ct) =>
        {
            var concurrent = Interlocked.Increment(ref active);
            try
            {
                Check.Require(concurrent == 1, "At most one exact-path provider read is in flight.");
                var index = Interlocked.Increment(ref reads) - 1;
                if (index >= gates.Length) return values[index];
                if (index == 1) arrived.TrySetResult(ct);
                return await (outcome == "stop" ? gates[index].Task.WaitAsync(ct) : gates[index].Task);
            }
            finally { Interlocked.Decrement(ref active); }
        });
        var a = barrier.ReadAsync("a/x", first.Token);
        var b = barrier.ReadAsync("a/x", left.Token);
        var c = barrier.ReadAsync("a/x", right.Token);
        try
        {
            Check.Require(reads == 1 && !b.IsCompleted && !c.IsCompleted,
                "Callers asking during an older read are held back without starting another request.");
            gates[0].SetResult(values[0]);
            Check.Require(ReferenceEquals(await a, values[0]), "The first producer receives its own result.");
            await Task.WhenAny(arrived.Task, Task.WhenAll(b, c)).WaitAsync(SignalWait);
            Check.Require(arrived.Task.IsCompleted && reads == 2,
                "Held-back callers require a later read, never the old single-flight answer.");
            var producer = await arrived.Task;
            var source = producer == left.Token ? left : right;
            var producerTask = producer == left.Token ? b : c;
            var followerTask = producer == left.Token ? c : b;
            if (outcome == "stop")
            {
                source.Cancel();
                Check.Require(await FailureAsync(producerTask) is OperationCanceledException &&
                    ReferenceEquals(await followerTask.WaitAsync(SignalWait), values[2]) && reads == 3,
                    "Only the producer's own stop allows the surviving caller a fresh replacement.");
                return;
            }
            Exception? failure = outcome switch
            {
                "item" or "cancel-item" => new ItemFailedException("a/x", TextKey.ReadDropboxFailed, null),
                "terminal" => new OperationCanceledException(),
                "cancel-aggregate" => new AggregateException(new IOException("terminal")),
                "cancel-foreign" => new OperationCanceledException(foreign.Token),
                _ => null
            };
            if (outcome.StartsWith("cancel-", StringComparison.Ordinal)) source.Cancel();
            if (failure is null) gates[1].SetResult(values[1]);
            else gates[1].SetException(failure);
            if (failure is null)
            {
                Check.Require(ReferenceEquals(await producerTask, values[1]), "The later producer receives its own result.");
                var followed = await followerTask.WaitAsync(SignalWait);
                Check.Require(reads is 2 or 3 && ReferenceEquals(followed, values[reads - 1]),
                    "A follower shares a later read or starts its own fresh read; starts never exceed callers.");
                var before = reads;
                var d = await Task.Run(() => barrier.ReadAsync("a/x", default)).WaitAsync(SignalWait);
                Check.Require(reads == before + 1 && ReferenceEquals(d, values[before]),
                    "A later caller starts a new read; completed results are never retained in the registry.");
            }
            else
            {
                Check.Require(ReferenceEquals(await FailureAsync(producerTask), failure),
                    "The later producer receives its terminal outcome unchanged: " + outcome);
                var followerFailure = await FailureAsync(followerTask);
                Check.Require(reads <= 3 && (ReferenceEquals(followerFailure, failure) ||
                    followerFailure is null && ReferenceEquals(await followerTask, values[2])),
                    "A follower receives a later terminal outcome or its own fresh result, never the older answer: " + outcome);
            }
        }
        finally
        {
            foreach (var gate in gates) gate.TrySetResult(values[2]);
            first.Cancel();
            left.Cancel();
            right.Cancel();
        }
    }

    private static async Task IndependentPathsAsync()
    {
        var gate = new TaskCompletionSource<Entry?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var paths = new List<string>();
        var barrier = new PathReadBarrier((path, _) => { paths.Add(path); return gate.Task; });
        var lower = barrier.ReadAsync("a/x", default);
        var upper = barrier.ReadAsync("a/X", default);
        try
        {
            Check.Require(paths.SequenceEqual(["a/x", "a/X"]), "Case-different paths start independent reads immediately.");
        }
        finally { gate.TrySetResult(null); }
        await Task.WhenAll(lower, upper);
    }

    private static async Task WaitingCancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var gate = new TaskCompletionSource<Entry?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var barrier = new PathReadBarrier((_, _) => { reads++; return gate.Task; });
        var producer = barrier.ReadAsync("a/x", default);
        var waiting = barrier.ReadAsync("a/x", cancellation.Token);
        try
        {
            cancellation.Cancel();
            Check.Require(await FailureAsync(waiting) is OperationCanceledException && reads == 1 && !producer.IsCompleted,
                "Cancelling a held-back caller neither cancels the producer nor starts a request.");
        }
        finally { gate.TrySetResult(null); }
        Check.Require(await producer is null && reads == 1, "The producer completes unaffected by its cancelled waiter.");
    }

    private static async Task<Exception?> FailureAsync(Task<Entry?> task)
    {
        try { await task.WaitAsync(SignalWait); return null; }
        catch (Exception failure) { return failure; }
    }
}
