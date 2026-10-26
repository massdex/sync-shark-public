using System.Diagnostics;
using System.Reflection;

namespace Massdex.SyncShark.V2;

/// <summary>Real processes and production peer validation; fake engine, isolated authority and no provider access.</summary>
internal static class AutoSyncProcessChecks
{
    internal static async Task<int> ChildAsync(string root, string address)
    {
        try
        {
            await using var host = AutoSyncBackgroundHost.Start(address,
                () => new FileStream(Path.Combine(root, "owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None),
                () => new Engine(root));
            await host.Completion;
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 10; }
    }

    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "host-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var address = "SyncShark.ProcessCheck." + Guid.NewGuid().ToString("N");
        using var first = Start(root, address);
        using var second = Start(root, address);
        Process? replacement = null;
        try
        {
            var client = new AutoSyncHostClient(address);
            var ready = await ReadyAsync(client);
            var owner = ready.Host.Pid == first.Id ? first : second;
            var loser = owner == first ? second : first;
            await loser.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Require(loser.ExitCode == 10, "the losing starter fails cleanly before creating an engine");
            Require(File.ReadAllLines(Path.Combine(root, "engines.log")).Length == 1, "exactly one engine across simultaneous starters");
            var request = AutoSyncHostClient.Request("now", "pair-1");
            var pending = client.TrySendAsync(request, TimeSpan.FromSeconds(3));
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                while (!File.Exists(Path.Combine(root, "commands.log"))) await Task.Delay(10, deadline.Token);
            Require(!pending.IsCompleted, "the host is killed with an admitted mutation awaiting its reply");
            owner.Kill(entireProcessTree: true);
            await owner.WaitForExitAsync();
            try { await pending; throw new InvalidOperationException("A killed mutation must report uncertainty."); }
            catch (AutoSyncHostUncertainException) { }
            replacement = Start(root, address);
            var restarted = await ReadyAsync(client);
            Require(restarted.Host.Pid == replacement.Id && restarted.Host.InstanceId != ready.Host.InstanceId,
                "restart acquires the released authority with a fresh engine identity");
            Require(File.ReadAllLines(Path.Combine(root, "engines.log")).Length == 2, "one engine per lifetime");
            Require(File.ReadAllLines(Path.Combine(root, "commands.log")).Length == 1, "restart never replays an earlier command");
            await client.TrySendAsync(AutoSyncHostClient.Request("stop"), TimeSpan.FromSeconds(3));
            await replacement.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Require(replacement.ExitCode == 0, "safe stop remains clean");
            Console.WriteLine("PASS real two-host race, trusted peers, kill/restart and no command replay");
        }
        finally
        {
            foreach (var process in new[] { first, second, replacement })
                if (process is not null && !process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            replacement?.Dispose();
        }
    }

    private static Process Start(string root, string address)
    {
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("Process path unavailable");
        var start = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = root };
        if (Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--autosync-host-child");
        start.ArgumentList.Add(root); start.ArgumentList.Add(address);
        return Process.Start(start) ?? throw new InvalidOperationException("Child did not start");
    }

    private static async Task<AutoSyncHostReply> ReadyAsync(AutoSyncHostClient client)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            var reply = await client.TrySendAsync(AutoSyncHostClient.Request("status"), TimeSpan.FromMilliseconds(300), deadline.Token);
            if (reply?.Ok == true) return reply;
            await Task.Delay(50, deadline.Token);
        }
    }

    private sealed class Engine : IAutoSyncHostEngine
    {
        private readonly string _root;
        internal Engine(string root) { _root = root; File.AppendAllText(Path.Combine(root, "engines.log"), Environment.ProcessId + "\n"); }
        public async Task<object> ExecuteAsync(AutoSyncHostRequest request, CancellationToken token)
        {
            if (request.Command == "now")
            {
                File.AppendAllText(Path.Combine(_root, "commands.log"), request.RequestId + "\n");
                await Task.Delay(Timeout.Infinite, token);
            }
            return new { running = true };
        }
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
