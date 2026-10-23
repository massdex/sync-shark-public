using System.ComponentModel;
using System.Security.Cryptography;
using Massdex.SyncShark;

internal static class DualSlotConcurrencyChecks
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "sync-shark-pilot-tests", "alternating-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
            var first = new DualSlotJsonStore<Payload>(instance.StateAuthority, "alternating-test");
            var second = new DualSlotJsonStore<Payload>(instance.StateAuthority, "alternating-test");
            await first.SaveAsync(new Payload { Value = "one" });
            await first.SaveAsync(new Payload { Value = "two" });
            using var opened = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var attempted = new ManualResetEventSlim();
            var readerThread = 0;
            Payload.Deserialized = () =>
            {
                if (Environment.CurrentManagedThreadId != Volatile.Read(ref readerThread)) return;
                opened.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Held state reader was not released.");
            };
            var reader = Task.Run(() =>
            {
                Volatile.Write(ref readerThread, Environment.CurrentManagedThreadId);
                return first.Load();
            });
            Task? writer = null;
            Exception? failure = null;
            try
            {
                if (!opened.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Real slot reader did not open.");
                writer = Task.Run(async () =>
                {
                    attempted.Set();
                    await second.SaveAsync(new Payload { Value = "three" });
                });
                if (!attempted.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Writer did not start.");
                // The actual deserializer holds the native read handle. A second store instance
                // must queue its entire read/select/write transaction, not hit ERROR_SHARING_VIOLATION.
                await Task.Delay(100);
                if (writer.IsCompleted)
                {
                    try { await writer; }
                    catch (IOException error) when (error.InnerException is Win32Exception native)
                    {
                        throw new InvalidDataException("Concurrent real state save failed: native error " + native.NativeErrorCode, error);
                    }
                    throw new InvalidDataException("Concurrent save bypassed the held read transaction.");
                }
            }
            catch (Exception error) { failure = error; }
            finally { release.Set(); }
            await reader;
            if (writer is not null)
            {
                try { await writer; }
                catch (Exception error) { failure ??= error; }
            }
            if (failure is not null) throw failure;
            Payload.Deserialized = null;
            if (first.Load()?.Value != "three") throw new InvalidDataException("Queued state save did not commit.");
            Console.WriteLine("PASS real alternating state handles serialize across store instances; queued save commits after reader releases");

            var slotA = Path.Combine(instance.StateAuthority.StateRoot, "alternating-test-a.json");
            var slotB = Path.Combine(instance.StateAuthority.StateRoot, "alternating-test-b.json");
            var beforeA = SHA256.HashData(File.ReadAllBytes(slotA));
            var beforeB = SHA256.HashData(File.ReadAllBytes(slotB));
            opened.Reset();
            release.Reset();
            attempted.Reset();
            using var cancellation = new CancellationTokenSource();
            Payload.Deserialized = () =>
            {
                if (Environment.CurrentManagedThreadId != Volatile.Read(ref readerThread)) return;
                opened.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Cancelled state reader was not released.");
            };
            reader = Task.Run(() =>
            {
                Volatile.Write(ref readerThread, Environment.CurrentManagedThreadId);
                return first.Load();
            });
            writer = null;
            try
            {
                if (!opened.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Cancellation reader did not open.");
                writer = Task.Run(async () =>
                {
                    attempted.Set();
                    await second.SaveAsync(new Payload { Value = "must not commit" }, cancellation.Token);
                });
                if (!attempted.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Cancellation writer did not start.");
                await Task.Delay(100);
                cancellation.Cancel();
            }
            finally { release.Set(); }
            await reader;
            var cancelled = false;
            try { if (writer is not null) await writer; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { cancelled = true; }
            Payload.Deserialized = null;
            if (!cancelled || !beforeA.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(slotA))) ||
                !beforeB.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(slotB))))
                throw new InvalidDataException("A save cancelled while queued changed state.");
            Console.WriteLine("PASS cancellation after waiting for the shared gate preserves both exact slots");

            var refused = false;
            using (var externalHolder = File.Open(slotA, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try { await second.SaveAsync(new Payload { Value = "must not bypass native sharing" }); }
                catch (IOException error) when (error.InnerException is Win32Exception { NativeErrorCode: 32 })
                {
                    refused = true;
                    if (!error.Message.Contains("alternating-test-a.json", StringComparison.Ordinal) ||
                        !error.Message.Contains("Windows error 32", StringComparison.Ordinal))
                        throw new InvalidDataException("Native refusal lost its precise safe diagnostic.");
                }
            }
            if (!refused || first.Load()?.Value != "three" ||
                !beforeA.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(slotA))) ||
                !beforeB.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(slotB))))
                throw new InvalidDataException("Native exclusive-holder refusal did not preserve exact state.");
            Console.WriteLine("PASS external exclusive native holders remain refused; precise error and both slots are preserved");
        }
        finally
        {
            Payload.Deserialized = null;
            Directory.Delete(root, recursive: true);
        }
    }

    public sealed class Payload
    {
        internal static Action? Deserialized;
        private string _value = string.Empty;
        public string Value
        {
            get => _value;
            set { _value = value; Deserialized?.Invoke(); }
        }
    }
}
