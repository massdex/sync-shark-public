using System.Net.Http;
using Massdex.SyncShark;

internal static class CliRunControlChecks
{
    internal static async Task<int> RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        using var http = new HttpClient(new RefuseNetwork());
        using var host = new SyncAuthorityRuntimeHost(instance.StateAuthority, http);
        await host.LoadPairsAsync();
        var checks = 0;
        void Require(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("Run control: " + name);
            checks++;
            Console.WriteLine("PASS " + name);
        }
        Require(!host.HasActiveRun && host.ActiveRun is null, "idle has no active identity");
        var first = await host.BeginRunForTestAsync(commandId: "command-11111111111111111111111111111111");
        try
        {
            Require(host.HasActiveRun && host.ActiveRun?.Id == "command-11111111111111111111111111111111",
                "actual host exposes bound job identity");
            Require(await host.SetActiveRunPausedAsync(true) == true && first.IsPaused, "pause applies to actual control");
            Require(await host.SetActiveRunPausedAsync(true) == true && first.IsPaused, "repeat pause does not toggle resume");
            Require(await host.SetActiveRunPausedAsync(false) == false && !first.IsPaused, "resume is explicit");
            Require(!await host.StopActiveRunAsync("command-not-this-job", TimeSpan.FromMilliseconds(100)) &&
                !first.IsStopRequested, "wrong exact target cannot stop active job");
            var timedOut = false;
            try { await host.StopActiveRunAsync(host.ActiveRun!.Id, TimeSpan.FromMilliseconds(25)); }
            catch (TimeoutException) { timedOut = true; }
            Require(timedOut && first.IsStopRequested && host.HasActiveRun,
                "stop timeout is not reported as completed checkpoint");
        }
        finally { host.EndRunForTest(first); }
        Require(!host.HasActiveRun && host.ActiveRun is null, "completed control clears its snapshot");
        var second = await host.BeginRunForTestAsync(commandId: "command-22222222222222222222222222222222");
        try
        {
            Require(!await host.StopActiveRunAsync("command-11111111111111111111111111111111",
                TimeSpan.FromMilliseconds(100)) && !second.IsStopRequested,
                "old job id cannot cancel replacement job");
            var stopped = host.StopActiveRunAsync(host.ActiveRun!.Id, TimeSpan.FromSeconds(2));
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (!second.IsStopRequested && DateTime.UtcNow < deadline) await Task.Delay(5);
            Require(second.IsStopRequested && !stopped.IsCompleted, "stop waits on the exact original checkpoint");
            host.EndRunForTest(second);
            var third = await host.BeginRunForTestAsync();
            try
            {
                Require(await stopped && host.HasActiveRun && !third.IsStopRequested,
                    "original stop completes without waiting for or stopping subsequent job");
                Require(host.ActiveRun!.Id != "command-22222222222222222222222222222222",
                    "unnamed operations still receive a unique active identity");
            }
            finally { host.EndRunForTest(third); }
        }
        finally { host.EndRunForTest(second); }
        Require(await host.SetActiveRunPausedAsync(true) is null, "idle pause reports no active job");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The actual public pair admission proof requires Windows.");
        await PairMutationAdmissionChecks.RunAsync(Path.Combine(root, "pair-mutation-admission"));
        Console.WriteLine("{\"result\":\"PASS\",\"checks\":" + checks + ",\"boundary\":\"real host control; isolated state; no network\"}");
        return 0;
    }

    private sealed class RefuseNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected network in run-control fixture.");
    }
}
