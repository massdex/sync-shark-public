namespace Massdex.SyncShark.V2;

internal static partial class ExecutorChecks
{
    /// <summary>Small result-buffer checks: 64 simultaneous producers, lossless drain, cancellation and disposal.</summary>
    public static async Task RunResultBufferChecksAsync()
    {
        await ResultBufferAsync(cancel: false, abandon: false);
        await ResultBufferAsync(cancel: true, abandon: false);
        await ResultBufferAsync(cancel: false, abandon: true);
        var (_, a, b, executor) = Rig(Settings(64));
        var folders = Enumerable.Range(0, 140).Select(i => Act(ActionKind.Delete, Side.B, "stale-" + i,
            expected: b.Seed("stale-" + i, "", ItemKind.Folder)) with { FrozenVictimSignature = "old-signature" }).ToArray();
        var skipped = await Apply(executor, Plan(folders), a, b).WaitAsync(HeldEffectTimeout);
        Require(skipped.Count == folders.Length && skipped.All(result => result.Outcome == Outcome.Skipped),
            "superseded results larger than capacity are delivered without pre-reader deadlock");
    }

    private static async Task ResultBufferAsync(bool cancel, bool abandon)
    {
        const int Workers = 64, Files = 320;
        // One result consumed, two waves queued, one wave awaiting publication.
        const int Saturated = 1 + Workers * 3;
        var (store, a, b, executor) = Rig(Settings(Workers));
        using var cancellation = new CancellationTokenSource();
        var firstWave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saturated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var overflow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admitted = 0;
        var writer = new RecordingWriter(b)
        {
            FreeSpaceAdmissionAsyncFor = async (_, ct) =>
            {
                var count = Interlocked.Increment(ref admitted);
                if (count == Workers) firstWave.TrySetResult();
                if (count == Saturated) saturated.TrySetResult();
                if (count > Saturated) overflow.TrySetResult();
                await release.Task.WaitAsync(ct);
                return FreeSpaceAdmission.Granted;
            }
        };
        var actions = Enumerable.Range(0, Files).Select(i => Act(ActionKind.CopyNew, Side.B, "buffer-" + i,
            source: a.Seed("buffer-" + i, "bytes"), key: "buffer-key-" + i)).ToArray();
        var reader = executor.ApplyAsync(Plan(actions), a, writer, cancellation.Token).GetAsyncEnumerator();
        var disposed = false;
        try
        {
            var first = reader.MoveNextAsync().AsTask();
            // All 64 must enter before any can finish: a serialized implementation times out here.
            await firstWave.Task.WaitAsync(HeldEffectTimeout);
            release.SetResult();
            Require(await first.WaitAsync(HeldEffectTimeout), "64 concurrent producers yield a first result");
            var results = new List<ActionResult> { reader.Current };
            await saturated.Task.WaitAsync(HeldEffectTimeout);
            // A negative assertion needs a short observation window, not a throughput benchmark. The old
            // unbounded channel reaches overflow immediately; the bounded channel cannot admit another action.
            var exceeded = false;
            try { await overflow.Task.WaitAsync(TimeSpan.FromMilliseconds(100)); exceeded = true; }
            catch (TimeoutException) { }
            Require(!exceeded, "a paused consumer bounds queued results and backpressures producers");
            if (abandon)
            {
                await reader.DisposeAsync().AsTask().WaitAsync(HeldEffectTimeout);
                disposed = true;
                Require(writer.Active == 0 && admitted <= Saturated,
                    "early disposal cancels blocked publications and joins every producer");
                return;
            }
            if (cancel) cancellation.Cancel();
            while (await reader.MoveNextAsync().AsTask().WaitAsync(HeldEffectTimeout)) results.Add(reader.Current);
            Require(results.All(result => result.Outcome == Outcome.Done) &&
                results.Select(result => result.ActionId).Distinct().Count() == results.Count,
                "buffered results are delivered once without drops");
            Require(results.Count == store.Baselines("p1").Count,
                "every durably committed result drains even after run cancellation");
            Require(cancel ? results.Count < Files : results.Count == Files,
                "cancellation stops launching; normal consumption completes the whole plan");
        }
        finally
        {
            release.TrySetResult();
            if (!disposed) await reader.DisposeAsync().AsTask().WaitAsync(HeldEffectTimeout);
        }
    }
}
