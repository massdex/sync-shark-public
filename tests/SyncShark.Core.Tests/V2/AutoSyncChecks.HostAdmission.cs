namespace Massdex.SyncShark.V2;

internal static partial class AutoSyncChecks
{
    private static async Task HostAdmissionChecksAsync()
    {
        var admission = new PairRunAdmission();
        using var first = await admission.EnterAsync("a", default);
        using var other = await admission.EnterAsync("b", default).WaitAsync(TimeSpan.FromSeconds(1));
        using var withdrawn = new CancellationTokenSource();
        var queued = admission.EnterAsync("a", withdrawn.Token);
        withdrawn.Cancel();
        try { await queued; throw new InvalidOperationException("cancelled admission returned a lease"); }
        catch (OperationCanceledException) { }
        var successor = admission.EnterAsync("a", default);
        Require(!successor.IsCompleted, "cancelled same-pair waiter does not release its predecessor");
        first.Dispose();
        using var next = await successor.WaitAsync(TimeSpan.FromSeconds(1));
        var maintenance = admission.EnterAsync(null, default);
        Require(!maintenance.IsCompleted, "all-pair maintenance joins existing pair owners");
        next.Dispose();
        other.Dispose();
        using var all = await maintenance.WaitAsync(TimeSpan.FromSeconds(1));
        var after = admission.EnterAsync("c", default);
        Require(!after.IsCompleted, "pair admission cannot pass all-pair maintenance");
        all.Dispose();
        (await after.WaitAsync(TimeSpan.FromSeconds(1))).Dispose();
        await admission.WithAsync("a", async () =>
        {
            using var nested = await admission.EnterAsync("a", default).WaitAsync(TimeSpan.FromSeconds(1));
            try
            {
                using var widened = await admission.EnterAsync(null, default);
                throw new Exception("pair scope widened without rejection");
            }
            catch (InvalidOperationException) { }
            return true;
        }, default);
        using (await admission.EnterAsync("a", default).WaitAsync(TimeSpan.FromSeconds(1))) { }

        var watch = new NonCooperativeWatch();
        var wake = await ChangeWait.AnyAsync([watch], Task.CompletedTask, null, TimeProvider.System, default)
            .WaitAsync(TimeSpan.FromSeconds(4));
        Require(wake.Kind == WakeKind.Fault && watch.Disposed, "losing wait is retired within the drain bound");
        watch.Done.TrySetResult();

        await using var rig = await Rig.StartedAsync();
        using var source = new HeadlessWake();
        using (var binding = new SystemWakeBinding(source, () => rig.Auto.Kick(null)))
        {
            source.Raise();
            await rig.ParkedAsync(AutoStatus.Waiting);
            rig.Clock.Advance(Settings.ChangeQuietWindow);
            await rig.ParkedAsync(AutoStatus.Idle);
            Require(rig.Run.Runs("p1") == 2, "headless shared wake binding triggers a run");
        }
        Require(source.Subscribers == 0, "headless wake binding detaches before shutdown");
        Pass("HostAdmissionChecks");
    }

    private sealed class NonCooperativeWatch : IChangeWatch
    {
        internal readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed;
        public Task ChangedAsync(CancellationToken ct) => Done.Task;
        public void Dispose() => Disposed = true;
    }

    private sealed class HeadlessWake : ISystemWakeSource
    {
        public event Action? Woke;
        internal int Subscribers => Woke?.GetInvocationList().Length ?? 0;
        internal void Raise() => Woke?.Invoke();
        public void Dispose() { }
    }
}
