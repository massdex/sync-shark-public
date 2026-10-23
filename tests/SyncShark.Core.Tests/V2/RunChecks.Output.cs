using System.Diagnostics;
using System.Text;

namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task Output_RegressionChecksAsync()
    {
        await Output_ReviewChecksAsync();
        var clock = new OutputClock();
        var sink = new RecordingProgress();
        var settings = V2Settings.Default with { HeartbeatInterval = TimeSpan.FromSeconds(30) };
        using (var activity = RunActivity.Begin(sink, settings, clock))
        {
            activity.Progress.Report(new RunProgress("Uploading", "a", 7, 12));
            await SyncByteProgressStream.ReportCurrentProcessAsync("arbitrary important notice", "detail", ProcessMessageKind.Notice);
            var notice = sink.Reports.Last();
            Require(notice.Phase == "Uploading" && notice.Done == 7 && notice.Total == 12 && notice.Path == "a",
                "notices retain active progress without keyword classification");
            clock.Advance(30);
            Require(sink.Reports.Count(x => x.Message?.StartsWith("Still working:") == true) == 1, "first heartbeat");
            clock.Advance(5);
            Require(sink.Reports.Count(x => x.Message?.StartsWith("Still working:") == true) == 1, "check does not repeat heartbeat early");
            clock.Advance(25);
            Require(sink.Reports.Count(x => x.Message?.StartsWith("Still working:") == true) == 2, "heartbeat repeats at its interval");
        }
        var warnings = new List<string>();
        using (var ops = RunOperations.Start(clock, settings, warnings.Add))
        {
            var old = RunOperations.Begin(RunOperationKind.Store, "old identity");
            var snapshot = ops.Summarize().Oldest!;
            old.Dispose();
            using var current = RunOperations.Begin(RunOperationKind.Store, "new identity");
            Require(snapshot.Label == "old identity", "released entry identity is never recycled");
            using var a = RunOperations.Begin(RunOperationKind.TransferSlot, "admission");
            using var b = RunOperations.Begin(RunOperationKind.TransferSlot, "worker");
            clock.Advance(1);
            Require(!warnings.Any(x => x.Contains("transfer slots")), "routine back-pressure stays silent");
            clock.Advance(16);
            clock.Advance(5);
            Require(warnings.Count(x => x.StartsWith("2 actions waiting for transfer slots; oldest: admission")) == 1, "slot waits are aggregated and throttled");
        }
        warnings.Clear();
        using (var ops = RunOperations.Start(clock, settings, warnings.Add))
        using (var response = new HttpResponseMessage { RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://example.test/2/files/download?secret=hidden") })
        {
            using var moving = RunOperationStream.Track(new MemoryStream(new byte[4]), response);
            for (var n = 0; n < 4; n++) { moving.ReadByte(); clock.Advance(20); }
            Require(warnings.Count == 0, "a moving long-lived body does not warn");
            var blocked = new BlockingReadStream();
            await using var body = RunOperationStream.Track(blocked, response);
            var pending = body.ReadAsync(new byte[1].AsMemory()).AsTask();
            clock.Advance(16);
            Require(warnings.Any(x => x.Contains("/2/files/download response body")) && warnings.All(x => !x.Contains("secret")),
                "a stalled body warns with its route only");
            blocked.Release.TrySetResult(0);
            await pending;
        }
        using (var gate = new SemaphoreSlim(0, 1))
        {
            var messages = new List<string>();
            var task = V2CliRuntime.WaitForDataLockAsync(gate, line =>
            {
                messages.Add(line);
                if (messages.Count == 3) gate.Release();
            }, CancellationToken.None, TimeSpan.FromMilliseconds(10));
            await task.WaitAsync(TimeSpan.FromSeconds(2));
            Require(messages.Count == 3 && messages[1].StartsWith("Still waiting for the Sync Shark data lock ("), "outer gate repeats timed wait announcements");
            gate.Release();
        }
        var root = Path.Combine(PhysicalFixtureRoot, "output-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var foreign = Path.Combine(root, "20260101-120000-000-aaaaaaaa-p-run.log");
            File.WriteAllText(foreign, "foreign");
            RunLog.Rotate(root, 0, 0);
            Require(File.Exists(foreign), "rotation refuses a non-dedicated directory");
            var folder = Path.Combine(root, "v2", "logs");
            Directory.CreateDirectory(folder);
            var unrelated = Path.Combine(folder, "unrelated.log");
            File.WriteAllText(unrelated, "foreign");
            RunLog.Rotate(folder, 0, 0);
            Require(File.Exists(unrelated), "rotation ignores non-run names");
            var error = Console.Error;
            try
            {
                Console.SetError(new ThrowingOutput());
                using var log = RunLog.Open(root, "boundary", "run", true);
                var output = RunLog.Sink(log, _ => throw new IOException("sink"));
                output("summary");
                log.Detailed("trace");
                RunOutput.Observe(new IOException("reporter"));
                using var gate = new SemaphoreSlim(0, 1);
                using var stop = new CancellationTokenSource(30);
                try { await V2CliRuntime.WaitForDataLockAsync(gate, _ => throw new IOException("sink"), stop.Token, TimeSpan.FromMilliseconds(5)); }
                catch (OperationCanceledException) { }
            }
            finally { Console.SetError(error); }

            // A blocked disk never blocks producers; Dispose only waits for its bounded join.
            using var release = new ManualResetEventSlim();
            using var entered = new ManualResetEventSlim();
            var writerThread = 0;
            var reservedText = new MemoryStream();
            var reservedJson = new MemoryStream();
            var blockedLog = RunLog.Open(root, "blocked", "run", false, path =>
            {
                writerThread = Environment.CurrentManagedThreadId;
                entered.Set(); release.Wait(); return path.EndsWith(".jsonl") ? reservedJson : reservedText;
            });
            Require(entered.Wait(TimeSpan.FromSeconds(2)), "background writer started");
            var watch = Stopwatch.StartNew();
            for (var n = 0; n < 11_000; n++) blockedLog.Detailed("trace");
            blockedLog.Summary("reserved summary");
            Require(blockedLog.DroppedLines >= 1000 && watch.Elapsed < TimeSpan.FromSeconds(1), "overflow is counted and producers do not wait for disk");
            watch.Restart();
            blockedLog.Dispose();
            Require(watch.Elapsed < TimeSpan.FromMilliseconds(5300), "dispose joins for at most five seconds without IO lock");
            release.Set();
            await blockedLog.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            Require(writerThread != 0, "only background task opens files");
            Require(Encoding.UTF8.GetString(reservedText.ToArray()).Contains("reserved summary"),
                "trace overflow cannot consume summary capacity");

            var textBuffer = new CountingOutputStream();
            var jsonBuffer = new CountingOutputStream();
            using (var encoded = RunLog.Open(root, "bytes", "run", false,
                path => path.EndsWith(".jsonl") ? jsonBuffer : textBuffer))
            {
                encoded.ByteLimit = 1_000;
                encoded.Summary(new string('界', 80));
                encoded.Summary(new string('界', 80));
                await Task.Delay(600);
                var flushes = textBuffer.Flushes;
                for (var n = 0; n < 20; n++) encoded.Summary("late " + n);
                await Task.Delay(100);
                Require(textBuffer.Flushes == flushes, "individual lines do not cause a flush");
            }
            Require(Encoding.UTF8.GetString(textBuffer.ToArray()).Contains("run log truncated") &&
                    Encoding.UTF8.GetString(jsonBuffer.ToArray()).Contains("run log truncated"),
                "encoded byte cap produces the terminal notice in both streams");

            using var failing = RunLog.Open(root, "failure", "run", false, _ => new FailingWriteStream());
            failing.Summary("must fail on background write");
            failing.Dispose();
            await failing.Completion;
            Require(failing.Failed, "writer enters explicit failed state");
            var delivered = false;
            RunLog.Sink(failing, _ => delivered = true)("console survives");
            Require(delivered, "console continues after file failure");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("PASS Output_RegressionChecks: notices, heartbeat, identities, waits, bodies, rotation, boundary, overflow, dispose, failure");
    }

    private sealed class FailingWriteStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("injected disk write failure");
        public override void Write(ReadOnlySpan<byte> buffer) => throw new IOException("injected disk write failure");
    }

    private sealed class CountingOutputStream : MemoryStream
    {
        internal int Flushes;
        public override void Flush() { Interlocked.Increment(ref Flushes); base.Flush(); }
    }

    private sealed class ThrowingOutput : StringWriter
    {
        public override void WriteLine(string? value) => throw new IOException("broken stderr");
    }

    private sealed class BlockingReadStream : MemoryStream
    {
        internal TaskCompletionSource<int> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => new(Release.Task);
    }

    private sealed class OutputClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        private readonly List<OutputTimer> _timers = [];
        public override DateTimeOffset GetUtcNow() => _now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _now.UtcTicks;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new OutputTimer(() => callback(state)); _timers.Add(timer); return timer;
        }
        internal void Advance(int seconds)
        {
            _now += TimeSpan.FromSeconds(seconds);
            foreach (var timer in _timers.ToArray()) timer.Fire();
        }
        private sealed class OutputTimer(Action callback) : ITimer
        {
            private bool _disposed;
            internal void Fire() { if (!_disposed) callback(); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
