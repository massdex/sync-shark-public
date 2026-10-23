using Massdex.SyncShark;
using System.Runtime.InteropServices;
using System.Security.Principal;

internal static class DiscoveryActivationChecks
{
    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows task permissions are required.");
        var count = 0; const string user = "S-1-5-21-1-2-3-1001";
        var expected = "O:BAG:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGX;;;" + user + ")";
        void Check(bool value, string label) { if (!value) throw new InvalidDataException(label); count++; Console.WriteLine("PASS " + label); }
        void Reject(string sddl, string label)
        {
            try { SyncDiscoveryClient.ValidateTaskSecurity(sddl, user); }
            catch (Exception error) when (error is IOException or ArgumentException) { Check(true, label); return; }
            throw new InvalidDataException("Expected rejection: " + label);
        }
        SyncDiscoveryClient.ValidateTaskSecurity(expected, user); Check(true, "Fixed task grants the exact user read/run but not write");
        Reject(expected.Replace("GRGX", "GA"), "A user-writable task cannot be started");
        Reject(expected.Replace("O:BA", "O:" + user), "User ownership cannot bypass the task DACL");
        Reject(expected.Replace("O:BA", "O:SY"), "Task ownership must match the fixed administrator-owned setup");
        Reject(expected.Replace("G:BA", "G:" + user), "Task primary group cannot drift to the interactive user");
        Reject(expected.Replace(user, "S-1-5-21-1-2-3-1002"), "Another user's task is refused");
        Reject(expected.Replace("D:P", "D:"), "Inherited task permissions are refused");
        Reject(expected + "(A;;GA;;;WD)", "Unexpected broad task permissions are refused");
        Reject(expected.Replace("(A;;GA;;;BA)", "(A;;GRGX;;;BA)"), "Changed administrator task control is refused");
        object? service = null, definition = null, principal = null;
        try
        {
            var currentSid = WindowsIdentity.GetCurrent().User!.Value;
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!);
            ((dynamic)service!).Connect(); definition = ((dynamic)service).NewTask(0);
            principal = ((dynamic)definition).Principal; ((dynamic)principal).UserId = currentSid;
            var returned = (string)((dynamic)principal).UserId;
            Check(returned != currentSid, "Real in-memory Task Scheduler definition reproduces account-name normalization");
            SyncDiscoveryClient.ValidateTaskUser(returned, currentSid);
            Check(true, "Runtime validates the real Windows-resolved task account before task execution");
            SyncDiscoveryClient.ValidateTaskUser(currentSid, currentSid);
            Check(true, "Exact SID representation also remains valid");
            foreach (var bad in new string?[] { null, "", "S-1-5-18", "S-1-invalid" })
            {
                var rejected = false;
                try { SyncDiscoveryClient.ValidateTaskUser(bad, currentSid); }
                catch (IOException) { rejected = true; }
                Check(rejected, "Missing, different or malformed task identity cannot start Discovery");
            }
        }
        finally
        {
            foreach (var item in new[] { principal, definition, service })
                if (item is not null && Marshal.IsComObject(item)) Marshal.FinalReleaseComObject(item);
        }
        if (!SyncDiscoveryClient.IsInstalled)
        {
            var result = await SyncDiscoveryClient.TryReadAsync([new(@"C:\NotARealDiscoveryRoot", 1, 10)], CancellationToken.None);
            Check(result is null, "Missing protected installation returns ordinary-scanner fallback without starting a task");
        }
        else Console.WriteLine("INFO Missing-installation case not run because the real protected component exists.");
        var fixture = Path.Combine(SyncSharkRuntimePaths.ProductRoot, "discovery-check");
        var local = Path.Combine(Path.GetDirectoryName(SyncSharkRuntimePaths.ProductRoot)!, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(local);
        try
        {
        using var instance = PilotInstanceLock.Acquire(Path.Combine(fixture, "state", "instance.lock"));
        using var http = new HttpClient(new RejectProviderRequests());
        using var host = new SyncAuthorityRuntimeHost(instance.StateAuthority, http);
        await host.Authority.InitializeAsync("discovery-check", "discovery-profile", "check", "{}");
        Directory.CreateDirectory(Path.Combine(local, "first"));
        Directory.CreateDirectory(Path.Combine(local, "second"));
        using var root = PilotDirectoryLease.Open(Path.Combine(local, "first"));
        using var secondRoot = PilotDirectoryLease.Open(Path.Combine(local, "second"));
        var pair = new SyncPair("discovery-pair", "First Account Pair", root.Path,
            root.Identity.VolumeSerialNumber, root.Identity.FileId, "/Check", "id:check",
            ConnectionId: "first-account");
        await host.Authority.RegisterPairAsync(pair.Id, 1);
        await host.Authority.RetainPairDefinitionAsync(pair, true);
        // ISS-1: the second fixture pair must name a GENUINELY DISTINCT remote folder identity and path. It
        // previously reused the first pair's Dropbox folder id while differing only by connection, which the
        // provider-global Same rule rightly refuses as a duplicate endpoint across pairs. The Same and
        // equal-endpoint production refusal is unchanged; only this stale fixture is corrected.
        var second = pair with { Id = "second-discovery-pair", Name = "Second Account Pair", ConnectionId = "second-account",
            LocalRoot = secondRoot.Path, LocalRootFileId = secondRoot.Identity.FileId,
            DropboxRoot = "/Check2", DropboxFolderId = "id:check2" };
        await host.Authority.RegisterPairAsync(second.Id, 1);
        await host.Authority.RetainPairDefinitionAsync(second, false);
        var missingRejected = false;
        try { await host.CheckFastDiscoveryAsync("not-retained"); }
        catch (InvalidOperationException) { missingRejected = true; }
        Check(missingRejected, "Read-only Discovery check accepts only an exact retained pair");
        if (!SyncDiscoveryClient.IsInstalled)
        {
            var before = System.Text.Json.JsonSerializer.Serialize(await host.Authority.LoadPairSetAsync());
            var result = await host.CheckFastDiscoveryAsync(second.Id);
            Check(result.PairName == second.Name && !result.Ready && result.MetadataEntries == 0,
                "Missing setup is reported for the requested account pair, without silently choosing another");
            Check(result.Status.Contains("Not Set Up", StringComparison.Ordinal),
                "A missing helper is explicit instead of a false ready result");
            Check(before == System.Text.Json.JsonSerializer.Serialize(await host.Authority.LoadPairSetAsync()),
                "Read-only Discovery check preserves both saved pair definitions");
        }
        await host.Authority.RetainPairDefinitionAsync(second with { LocalRootFileId = second.LocalRootFileId + 1 }, false);
        var identityRejected = false;
        try { await host.CheckFastDiscoveryAsync(second.Id); }
        catch (Exception error) when (error is IOException or InvalidOperationException) { identityRejected = true; }
        Check(identityRejected, "Changed local root identity is rejected before asking the helper to run");
        await host.Authority.RetainPairDefinitionAsync(second with { FilesOnDemandEnabled = true }, false);
        var cloud = await host.CheckFastDiscoveryAsync(second.Id);
        Check(!cloud.Ready && cloud.Status.Contains("Files On Demand", StringComparison.Ordinal),
            "Windows Files On Demand is distinguished from accelerated ordinary-pair discovery");
        }
        finally { Directory.Delete(local, recursive: true); }
        Console.WriteLine($"{count} Discovery activation-guard checks passed; no task registration or privileged operation.");
    }

    private sealed class RejectProviderRequests : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new InvalidOperationException("A local Discovery check must not contact a provider.");
    }
}
