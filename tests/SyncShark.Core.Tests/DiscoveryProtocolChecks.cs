using System.Buffers.Binary;
using System.Text;
using Massdex.SyncShark;

internal static class DiscoveryProtocolChecks
{
    private sealed record Entry(uint Root, ulong Id, ulong Parent, string Path, FileAttributes Attributes = FileAttributes.Normal, uint Links = 1);
    private static readonly SyncDiscoveryRoot[] Roots = [new(@"C:\FixtureA", 123, 10), new(@"C:\FixtureB", 123, 20)];
    private static readonly byte[] Nonce = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
    private static readonly Entry[] Entries = [new(0, 11, 10, "Reports", FileAttributes.Directory), new(0, 12, 11, @"Reports\Ä.txt"), new(1, 21, 20, "Second.txt")];
    private static int _passed;
    internal static async Task ReadNativeAsync(string directory)
    {
        _passed = 0;
        var requestPath = Path.Combine(directory, "request.bin");
        if (new FileInfo(requestPath).Length > 128 * 65536)
            throw new InvalidDataException("Native fixture request is too large.");
        var bytes = await File.ReadAllBytesAsync(requestPath);
        using var request = new BinaryReader(new MemoryStream(bytes), new UnicodeEncoding(false, false, true));
        Check(request.ReadBytes(8).AsSpan().SequenceEqual("SSDISC01"u8) && request.ReadUInt32() == 1,
            "Actual native request uses the managed protocol identity");
        var nonce = request.ReadBytes(32); var count = request.ReadUInt32(); var serial = request.ReadUInt32();
        Check(count == 2, "Native fixture carries the two real roots");
        var roots = new List<SyncDiscoveryRoot>();
        for (var index = 0; index < count; index++)
        {
            var id = request.ReadUInt64(); var units = request.ReadUInt32();
            if (units is < 3 or > 32760) throw new InvalidDataException("Bad fixture root length.");
            var name = request.ReadBytes(checked((int)units * 2));
            if (name.Length != units * 2) throw new EndOfStreamException();
            roots.Add(new(new UnicodeEncoding(false, false, true).GetString(name), serial, id));
        }
        Check(SyncDiscoveryProtocol.Request(roots, nonce).AsSpan().SequenceEqual(bytes),
            "Managed request encoder matches every native byte");
        await using var response = File.OpenRead(Path.Combine(directory, "response.bin"));
        var result = await SyncDiscoveryProtocol.ReadAsync(response, roots, nonce, CancellationToken.None);
        Check(result.Roots[0].Items.Count == 3 && result.Roots[1].Items.Count == 1,
            "Production managed reader accepts all four actual native metadata records");
        Check(result.Roots[0].Items["Ä.txt"].Size == 7 && result.Roots[0].Items["Reports/Today.txt"].Size == 7 &&
            result.Roots[1].Items["Second.txt"].Size == 7,
            "Native file sizes, Unicode names and nested ancestry reach the correct roots");
        Check(!result.IsCompletePathInventory && result.Before.JournalId == 7 && result.After.NextUsn == 25,
            "Native completion preserves the journal bounds without claiming complete-name authority");
        Console.WriteLine($"{_passed} cross-language native-response checks passed; synthetic MFT records, real filesystem metadata, no elevated worker.");
    }
    internal static async Task RunAsync()
    {
        _passed = 0;
        var request = SyncDiscoveryProtocol.Request(Roots, Nonce);
        Check(request.AsSpan(0, 8).SequenceEqual("SSDISC01"u8) && BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(44)) == 2,
            "Request binds one nonce and both admitted roots");
        Check(SyncDiscoveryProtocol.NewNonce().Length == 32, "Each real request can use a fresh cryptographic nonce");
        var valid = Response(Entries);
        var result = await Read(valid);
        var pipe = new AcknowledgedResponse(valid);
        await SyncDiscoveryProtocol.ReadAsync(pipe, Roots, Nonce, CancellationToken.None, acknowledgePipe: true);
        Check(pipe.Acknowledged, "Live-pipe mode acknowledges only a validated terminal frame before requiring EOF");
        var incompletePipe = new AcknowledgedResponse(valid[..^1]);
        await Reject(() => SyncDiscoveryProtocol.ReadAsync(incompletePipe, Roots, Nonce, CancellationToken.None, acknowledgePipe: true),
            "A partial live-pipe response cannot complete the acknowledgement exchange");
        Check(!incompletePipe.Acknowledged, "Truncated responses are never acknowledged");
        Check(result.Roots.Count == 2 && result.Roots[0].Items.ContainsKey("Reports/ä.txt") && result.Roots[1].Items.Count == 1,
            "Production response reader keeps Unicode metadata within the correct root");
        Check(!result.IsCompletePathInventory, "MFT metadata is never labelled complete deletion authority");
        var updates = new List<SyncDiscoveryProgress>(); var delivered = new List<SyncDiscoveryProgress>(); var activity = 0;
        var progressing = WithProgress(valid, (2u, 1UL, 1024UL), (2u, 9UL, 4096UL), (4u, 12UL, 8192UL), (7u, 20UL, 9000UL));
        var progressResult = await SyncDiscoveryProtocol.ReadAsync(new MemoryStream(progressing), Roots, Nonce,
            CancellationToken.None, onProgress: updates.Add, onActivity: () => activity++,
            onProgressAsync: async progress => { await Task.Yield(); delivered.Add(progress); });
        Check(delivered.SequenceEqual(updates), "Every validated live index progress frame is awaited by the operation-log consumer");
        Check(updates.Count == 4 && updates[^1].RecordsRead == 9000 && activity == 8 &&
            progressResult.Roots[0].Items.Count == 2 && !progressResult.IsCompletePathInventory,
            "Validated progress refreshes the idle watchdog without becoming metadata or a successful result");
        await Reject(() => Read(WithProgress(valid, (2u, 1UL, 10UL), (2u, 1UL, 10UL))), "Replayed progress cannot keep a stalled request alive");
        await Reject(() => Read(WithProgress(valid, (4u, 2UL, 10UL), (2u, 3UL, 20UL))), "Progress stages cannot move backwards");
        await Reject(() => Read(WithProgress(valid, (2u, 1UL, 20UL), (2u, 2UL, 10UL))), "Read-record counts cannot regress");
        await Reject(() => Read(WithProgress(valid, (8u, 1UL, 10UL))), "Unknown progress stages are rejected");
        await Reject(() => Read(WithProgress(valid[..52], (2u, 1UL, 10UL))), "Progress without a terminal inventory never succeeds");
        await Reject(() => Read(WithProgress(valid, Enumerable.Range(1, 4097).Select(i => (2u, (ulong)i, (ulong)i)).ToArray())),
            "Progress flooding is bounded independently of retained inventory");
        Check(SyncDiscoveryProtocol.MaximumDuration > TimeSpan.FromMinutes(5) &&
            SyncDiscoveryProtocol.IdleTimeout < SyncDiscoveryProtocol.MaximumDuration &&
            SyncDiscoveryProtocol.TaskDuration > SyncDiscoveryProtocol.MaximumDuration,
            "Long progressing scans have a separate idle guard and a later outer task deadline");
        var wrongNonce = valid.ToArray(); wrongNonce[12] ^= 1;
        await Reject(() => Read(wrongNonce), "A response for another request is rejected");
        await Reject(() => Read(valid[..^1]), "A truncated terminal frame cannot publish a partial inventory");
        await Reject(() => Read([.. valid, 1]), "Trailing frames after completion are rejected");
        await Reject(() => Read(Response([.. Entries, Entries[1] with { Id = 99, Path = @"Reports\ä.txt" }])), "Case aliases cannot create competing paths");
        await Reject(() => Read(Response([Entries[1] with { Path = @"..\escape.txt" }])), "Traversal paths cannot escape an admitted root");
        await Reject(() => Read(Response([Entries[1]])), "Missing parent directories invalidate the response");
        await Reject(() => Read(Response([Entries[0], Entries[1] with { Parent = 999 }])), "Parent identity must match the returned directory");
        await Reject(() => Read(Response([Entries[0] with { Attributes = FileAttributes.Directory | FileAttributes.ReparsePoint }])), "Unsafe reparse metadata falls back instead of being followed");
        await Reject(() => Read(Response(Entries, afterJournal: 8)), "Journal replacement discards the whole result");
        await Reject(() => Read(Response(Entries, afterFirst: 21)), "A journal gap invalidates even an unchanged journal identifier");
        var huge = valid.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(huge.AsSpan(52), uint.MaxValue);
        await Reject(() => Read(huge), "Oversized frames are rejected before allocation");
        await Reject(() => Read(valid, new CancellationToken(true)), "Cancellation cannot return accepted metadata");
        await Reject(() => Task.FromResult(SyncDiscoveryProtocol.Request([Roots[0], Roots[0]], Nonce)), "Duplicate root requests are refused");
        await CheckClientDeadlinesAsync(valid);
        Console.WriteLine($"{_passed} Discovery protocol checks passed; no native worker or privileged IPC was activated.");
    }
    private static async Task CheckClientDeadlinesAsync(byte[] valid)
    {
        var clock = new ControlledClock(); var activities = 0;
        var advancing = WithProgress(valid, Enumerable.Range(1, 7).Select(i => (2u, (ulong)i, (ulong)i * 1024)).ToArray());
        var result = await SyncDiscoveryClient.ReadWithDeadlineAsync((token, activity) =>
            SyncDiscoveryProtocol.ReadAsync(new MemoryStream(advancing), Roots, Nonce, token,
                onProgress: _ => clock.Advance(TimeSpan.FromMinutes(1)), onActivity: () => { activities++; activity(); }), CancellationToken.None, clock);
        Check(clock.Elapsed == TimeSpan.FromMinutes(7) && activities == 11 && result.Roots.Count == 2,
            "Real client timer wiring accepts seven advancing minutes through the real response parser");
        Check(clock.ActiveTimers == 0, "Successful client completion disposes both watchdog timers");
        clock = new ControlledClock();
        await Reject(() => SyncDiscoveryClient.ReadWithDeadlineAsync((token, activity) =>
        {
            clock.Advance(SyncDiscoveryProtocol.IdleTimeout); token.ThrowIfCancellationRequested();
            return Read(valid, token);
        }, CancellationToken.None, clock), "A silent transport is cancelled by the actual client idle timer");
        clock = new ControlledClock();
        var tooLong = WithProgress(valid, Enumerable.Range(1, 32).Select(i => (2u, (ulong)i, (ulong)i * 1024)).ToArray());
        await Reject(() => SyncDiscoveryClient.ReadWithDeadlineAsync((token, activity) =>
            SyncDiscoveryProtocol.ReadAsync(new MemoryStream(tooLong), Roots, Nonce, token,
                onProgress: _ => clock.Advance(TimeSpan.FromMinutes(1)), onActivity: activity), CancellationToken.None, clock),
            "Advancing parser activity cannot postpone the client's independent hard ceiling");
        clock = new ControlledClock(); activities = 0;
        await Reject(() => SyncDiscoveryClient.ReadWithDeadlineAsync((token, activity) =>
            SyncDiscoveryProtocol.ReadAsync(new MemoryStream(WithProgress(valid, (2u, 1UL, 1024UL), (2u, 1UL, 1024UL))), Roots, Nonce, token,
                onActivity: () => { activities++; activity(); }), CancellationToken.None, clock),
            "Replayed progress is rejected in the real parser connected to the client watchdog");
        Check(activities == 2 && clock.ActiveTimers == 0, "Rejected progress never refreshes the timer and failure releases the timers");
        clock = new ControlledClock(); using var cancel = new CancellationTokenSource();
        await Reject(() => SyncDiscoveryClient.ReadWithDeadlineAsync((token, activity) =>
            SyncDiscoveryProtocol.ReadAsync(new MemoryStream(advancing), Roots, Nonce, token,
                onProgress: _ => cancel.Cancel(), onActivity: activity), cancel.Token, clock),
            "Cancellation during a live read reaches the real linked token and returns no partial inventory");
        result = await SyncDiscoveryClient.ReadWithDeadlineAsync((token, activity) =>
            SyncDiscoveryProtocol.ReadAsync(new MemoryStream(valid), Roots, Nonce, token, onActivity: activity), CancellationToken.None, clock);
        Check(result.Roots.Count == 2 && clock.ActiveTimers == 0, "A fresh request succeeds after cancellation without inheriting expired timers");
    }
    private sealed class ControlledClock : TimeProvider
    {
        private readonly List<ClockTimer> timers = [];
        internal TimeSpan Elapsed { get; private set; }
        internal int ActiveTimers => timers.Count(timer => !timer.Disposed);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + Elapsed;
        public override long GetTimestamp() => Elapsed.Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new ClockTimer(this, callback, state); timers.Add(timer); timer.Change(dueTime, period); return timer; }
        internal void Advance(TimeSpan delta)
        {
            Elapsed += delta;
            foreach (var timer in timers.ToArray()) timer.FireIfDue();
        }
        private sealed class ClockTimer(ControlledClock clock, TimerCallback callback, object? state) : ITimer
        {
            private TimeSpan due = Timeout.InfiniteTimeSpan, period = Timeout.InfiniteTimeSpan;
            internal bool Disposed { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan periodValue)
            { if (Disposed) return false; due = dueTime == Timeout.InfiniteTimeSpan ? dueTime : clock.Elapsed + dueTime; period = periodValue; return true; }
            internal void FireIfDue()
            {
                if (Disposed || due == Timeout.InfiniteTimeSpan || due > clock.Elapsed) return;
                due = period == Timeout.InfiniteTimeSpan ? Timeout.InfiniteTimeSpan : clock.Elapsed + period;
                callback(state);
            }
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    private static byte[] Response(Entry[] entries, ulong afterJournal = 7, long afterFirst = 1)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.Unicode, true);
        writer.Write("SSDISC01"u8); writer.Write(1u); writer.Write(Nonce); writer.Write(2u); writer.Write(123u);
        foreach (var entry in entries)
        {
            var name = Encoding.Unicode.GetBytes(entry.Path);
            writer.Write((uint)(60 + name.Length)); writer.Write(1u); writer.Write(entry.Root);
            writer.Write(entry.Id); writer.Write(entry.Parent); writer.Write(5L); writer.Write((uint)entry.Attributes); writer.Write(entry.Links);
            writer.Write(7L); writer.Write(new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc).ToFileTimeUtc());
            writer.Write((uint)(name.Length / 2)); writer.Write(name);
        }
        writer.Write(72u); writer.Write(2u); writer.Write((uint)entries.Length);
        writer.Write(7UL); writer.Write(1L); writer.Write(20L); writer.Write(1L);
        writer.Write(afterJournal); writer.Write(afterFirst); writer.Write(25L); writer.Write(1L);
        return stream.ToArray();
    }
    private static byte[] WithProgress(byte[] response, params (uint Stage, ulong Sequence, ulong Records)[] updates)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.Unicode, true);
        writer.Write(response.AsSpan(0, 52));
        foreach (var update in updates)
        { writer.Write(24u); writer.Write(4u); writer.Write(update.Stage); writer.Write(update.Sequence); writer.Write(update.Records); }
        writer.Write(response.AsSpan(52)); return stream.ToArray();
    }
    private static Task<SyncDiscoveryResult> Read(byte[] value, CancellationToken token = default) =>
        SyncDiscoveryProtocol.ReadAsync(new MemoryStream(value, writable: false), Roots, Nonce, token);
    private sealed class AcknowledgedResponse(byte[] value) : MemoryStream(value, writable: false)
    {
        internal bool Acknowledged;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Position != Length || bytes.Length != 1 || bytes.Span[0] != 0xa5)
                throw new InvalidDataException("Invalid or premature native acknowledgement.");
            Acknowledged = true; return ValueTask.CompletedTask;
        }
    }
    private static void Check(bool good, string name) { if (!good) throw new InvalidOperationException(name); _passed++; Console.WriteLine("PASS " + name); }
    private static async Task Reject(Func<Task> action, string name)
    {
        try { await action(); }
        catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException)
        { _passed++; Console.WriteLine("PASS " + name); return; }
        throw new InvalidOperationException("Expected rejection: " + name);
    }
}
