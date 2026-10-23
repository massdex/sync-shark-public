using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Massdex.SyncShark;
using Windows.Storage.Provider;

namespace SyncShark.UiFixture;

internal static class NativeExplorerChecks
{
    internal static void ReadSearchScope()
    {
        // Read-only calls from the Windows SDK SearchAPI interfaces. No index rule,
        // crawl, reset or save operation is exposed by these delegates.
        var initialized = CoInitializeEx(IntPtr.Zero, 0);
        Marshal.ThrowExceptionForHR(initialized);
        IntPtr manager = IntPtr.Zero, catalog = IntPtr.Zero, scope = IntPtr.Zero;
        try
        {
            var clsid = new Guid("7D096C5F-AC08-4F1F-BEB7-5C22C517CE39");
            var iid = new Guid("AB310581-AC80-11D1-8DF3-00C04FB6EF69");
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, IntPtr.Zero, 5, ref iid, out manager));
            Marshal.ThrowExceptionForHR(Method<GetCatalog>(manager, 10)(manager, "SystemIndex", out catalog));
            Marshal.ThrowExceptionForHR(Method<GetScope>(catalog, 28)(catalog, out scope));
            var included = Method<Included>(scope, 13);
            foreach (var (label, path) in new[] {
                ("Temp", Path.GetTempPath()), ("User profile", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
                ("Proposed test", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "sync-shark-cf-40cfa42f-f91c-433c-90cf-4dd5bd66147b")) })
            {
                var uri = new Uri(Path.GetFullPath(path).TrimEnd('\\') + "\\").AbsoluteUri;
                Marshal.ThrowExceptionForHR(included(scope, uri, out var result));
                Console.WriteLine("NATIVE Search scope " + label + " included=" + (result != 0));
            }
        }
        finally
        {
            if (scope != IntPtr.Zero) Marshal.Release(scope);
            if (catalog != IntPtr.Zero) Marshal.Release(catalog);
            if (manager != IntPtr.Zero) Marshal.Release(manager);
            CoUninitialize();
        }
    }
    private static T Method<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetCatalog(IntPtr instance, [MarshalAs(UnmanagedType.LPWStr)] string name, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetScope(IntPtr instance, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Included(IntPtr instance, [MarshalAs(UnmanagedType.LPWStr)] string url, out int result);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr result);

    internal static void CleanAbandonedFixtures()
    {
        // The fixture owner is whichever account created the registration, so read the current
        // identity at runtime. No SID is committed and the check works on any machine.
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrEmpty(sid)) throw new IOException("The current fixture owner cannot be identified.");
        var fixtures = new Dictionary<string, string>
        {
            ["f00d0000000000000000000000000009"] = "f00d000000000000000000000000000e",
            ["f00d000000000000000000000000001a"] = "f00d0000000000000000000000000017",
            ["f00d0000000000000000000000000015"] = "f00d000000000000000000000000000d",
            ["f00d0000000000000000000000000021"] = "f00d0000000000000000000000000004",
            ["f00d0000000000000000000000000020"] = "f00d000000000000000000000000000f"
        };
        const string registryRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager\";
        foreach (var fixture in fixtures)
        {
            var id = "Massdex.SyncShark!" + sid + "!fixture-" + fixture.Key;
            var expected = Path.Combine(Path.GetTempPath(), "sync-shark-cf-" + fixture.Value);
            using (var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(registryRoot + id))
            {
                if (root is null) continue;
                using var paths = root.OpenSubKey("UserSyncRoots");
                Require(paths?.GetValue(sid) as string == expected && !Directory.Exists(expected) && !File.Exists(expected) &&
                    root.GetValue("DisplayNameResource") as string == "Sync Shark — Isolated Explorer Check (Fixture Account)",
                    "Abandoned fixture registration is exactly bound to its absent disposable root");
            }
            StorageProviderSyncRootManager.Unregister(id);
            using var remaining = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(registryRoot + id);
            Require(remaining is null, "Abandoned test-only shell registration removed");
        }
    }

    internal static async Task ProbeShellFirstAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "sync-shark-cf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var id = "Massdex.SyncShark!" + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value + "!fixture-" + Guid.NewGuid().ToString("N");
        try
        {
            var info = new StorageProviderSyncRootInfo { Id = id, Path = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(folder),
                DisplayNameResource = "Sync Shark Isolated Shell Probe", IconResource = ExplorerIntegration.InstalledExecutable + ",0",
                Version = "0.3.18", HydrationPolicy = StorageProviderHydrationPolicy.Full, PopulationPolicy = StorageProviderPopulationPolicy.AlwaysFull,
                HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.AutoDehydrationAllowed,
                InSyncPolicy = StorageProviderInSyncPolicy.FileLastWriteTime, AllowPinning = true };
            info.Context = Windows.Security.Cryptography.CryptographicBuffer.CreateFromByteArray(
                WindowsCloudFileIdentity.Root(id.Split('!')[2]).Serialize());
            StorageProviderSyncRootManager.Register(info);
            WriteRootMetadata(folder, "shell-first");
            await Task.Delay(1000);
            Console.WriteLine("NATIVE enumerated root count: " + StorageProviderSyncRootManager.GetCurrentSyncRoots().Count);
            try
            {
                var byFolder = StorageProviderSyncRootManager.GetSyncRootInformationForFolder(info.Path);
                Console.WriteLine("NATIVE by-folder ID match: " + (byFolder.Id == id));
            }
            catch (Exception error) { Console.WriteLine("NATIVE by-folder HRESULT " + error.HResult.ToString("X8")); }
            var result = StorageProviderSyncRootManager.GetSyncRootInformationForId(id);
            Require(result.Path.Path == folder, "Native shell-first registration can be read back");
        }
        finally
        {
            StorageProviderSyncRootManager.Unregister(id);
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any() &&
                (File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0) Directory.Delete(folder);
        }
    }

    internal static async Task RunAsync(string root, bool partialCleanupOnly = false, bool approvedLocation = false)
    {
        // Use the same bounded native-root allocation as the established Core CFAPI contract.
        // Generated build directories are not assumed to be eligible cloud roots.
        var folder = approvedLocation
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "sync-shark-cf-40cfa42f-f91c-433c-90cf-4dd5bd66147b")
            : Path.Combine(Path.GetTempPath(), "sync-shark-cf-" + Guid.NewGuid().ToString("N"));
        if (approvedLocation)
        {
            // Exact one-run location release com:2025; never an arbitrary path override.
            var drive = new DriveInfo(Path.GetPathRoot(folder)!);
            Require(!File.Exists(folder) && !Directory.Exists(folder) && drive.DriveFormat == "NTFS" &&
                drive.AvailableFreeSpace >= 10L * 1024 * 1024 * 1024, "Approved location is absent on NTFS with reserve intact");
            for (var ancestor = Path.GetDirectoryName(folder); ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
                Require((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) == 0,
                    "Approved location has no redirected ancestor");
            Require(!StorageProviderSyncRootManager.GetCurrentSyncRoots().Any(item =>
                folder.Equals(item.Path.Path, StringComparison.OrdinalIgnoreCase) ||
                folder.StartsWith(item.Path.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)),
                "Approved location is outside existing Shell provider roots");
        }
        if (Directory.Exists(folder)) throw new IOException("Native Explorer fixture must be a new empty allocation.");
        Directory.CreateDirectory(folder);
        PilotDirectoryIdentity identity;
        using (var lease = PilotDirectoryLease.Open(folder)) identity = lease.Identity;
        if (approvedLocation)
            Console.WriteLine($"NATIVE approved fixture identity volume={identity.VolumeSerialNumber} file={identity.FileId}");
        var pair = new SyncPair("fixture-" + Guid.NewGuid().ToString("N"), "Isolated Explorer Check", folder,
            identity.VolumeSerialNumber, identity.FileId, "/Fixture", "id:fixture", FilesOnDemandEnabled: true);
        var id = ExplorerIntegration.RootId(pair);
        Require(!StorageProviderSyncRootManager.GetCurrentSyncRoots().Any(item => item.Id == id), "Native fixture root is not an existing registration");
        var remote = DispatchProxy.Create<ISyncRemoteStore, EmptyExplorerRemote>();
        using var provider = new WindowsCloudFilesProvider(pair, remote, Path.Combine(root, "native-stage"));
        var file = Path.Combine(folder, "sample.txt"); await File.WriteAllTextAsync(file, "ordinary fixture bytes");
        try
        {
            provider.RegisterAndConnect();
            Console.WriteLine("PASS Native CFAPI provider connected before Explorer metadata registration");
            Console.WriteLine("NATIVE Root metadata before Explorer: " + WindowsCloudFilesProvider.IsOwnedSyncRoot(folder, pair.Id));
            Require(WindowsCloudFilesProvider.IsOwnedSyncRoot(folder, pair.Id), "CFAPI retained the exact fixture ownership before shell registration");
            WriteRootMetadata(folder, "before");
            try { await InvokeAsync("RegisterRootCoreAsync", pair, "Fixture Account"); }
            catch (IOException) when (partialCleanupOnly)
            { Console.WriteLine("PASS Reproduced non-enumerable native Shell registration"); }
            finally
            {
                WriteRootMetadata(folder, "after");
                try
                {
                    var unleased = StorageProviderSyncRootManager.GetSyncRootInformationForId(id);
                    Console.WriteLine("NATIVE registration readable after directory lease closes: " + (unleased?.Id == id));
                }
                catch (Exception error) { Console.WriteLine("NATIVE unleased readback HRESULT " + error.HResult.ToString("X8")); }
            }
            if (partialCleanupOnly)
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager\" + id))
                    Require(key is not null, "Partial native registration really exists before cleanup");
                try { Invoke("UnregisterRootCore", pair with { LocalRoot = folder + "-wrong" }); throw new IOException("Mismatched root was accepted."); }
                catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException)
                { Console.WriteLine("PASS Partial-root cleanup rejects a different saved path"); }
                try { Invoke("UnregisterRootCore", pair); throw new IOException("Managed root was detached without hydration."); }
                catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException)
                { Console.WriteLine("PASS Partial-root cleanup refuses a still-managed cloud root"); }
                await provider.UnregisterAsync(); Invoke("UnregisterRootCore", pair);
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager\" + id))
                    Require(key is null, "Production cleanup removes the non-enumerable native registration");
                Require(await File.ReadAllTextAsync(file) == "ordinary fixture bytes", "Partial-root cleanup preserves exact local bytes");
                Invoke("UnregisterRootCore", pair);
                Console.WriteLine("PASS Partial-root cleanup is safe to repeat");
                return;
            }
            var registration = StorageProviderSyncRootManager.GetCurrentSyncRoots().Single(item => item.Id == id);
            Require(StorageProviderSyncRootManager.GetSyncRootInformationForFolder(
                await Windows.Storage.StorageFolder.GetFolderFromPathAsync(folder)).Id == id,
                "Explorer by-folder readback identifies the exact native root");
            Require(registration.Path.Path.Equals(folder, StringComparison.OrdinalIgnoreCase) &&
                registration.DisplayNameResource.Contains("Fixture Account") && registration.AllowPinning &&
                registration.StorageProviderItemPropertyDefinitions.Any(item => item.Id == 1) &&
                WindowsCloudFilesProvider.IsOwnedSyncRoot(folder, pair.Id), "Explorer registers exact account metadata without losing CFAPI ownership");
            await InvokeAsync("RegisterRootCoreAsync", pair, "Fixture Account Renamed");
            Require(StorageProviderSyncRootManager.GetCurrentSyncRoots().Count(item => item.Id == id) == 1,
                "Registration refresh does not create duplicate roots");
            await InvokeAsync("SetItemStatusCoreAsync", pair, "sample.txt", "Needs Attention", "Fixture Account");
            Require(await File.ReadAllTextAsync(file) == "ordinary fixture bytes", "Native custom status publication preserves file bytes");
            try { Invoke("UnregisterRootCore", pair); throw new IOException("A managed root was removed without hydration."); }
            catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException)
            { Console.WriteLine("PASS Explorer unregistration refuses a still-managed cloud root"); }
            await provider.UnregisterAsync(); Invoke("UnregisterRootCore", pair);
            Require(!StorageProviderSyncRootManager.GetCurrentSyncRoots().Any(item => item.Id == id) &&
                !WindowsCloudFilesProvider.IsOwnedSyncRoot(folder, pair.Id) && await File.ReadAllTextAsync(file) == "ordinary fixture bytes",
                "Native unregister leaves no root entry and preserves local files");
        }
        finally
        {
            if (WindowsCloudFilesProvider.IsOwnedSyncRoot(folder, pair.Id)) await provider.UnregisterAsync();
            // Use the exact fixture ID even if a partially written shell entry is not enumerable.
            try { StorageProviderSyncRootManager.Unregister(id); }
            catch (Exception error) when (error.HResult == unchecked((int)0x80070490)) { }
            using (var retained = PilotDirectoryLease.Open(folder, identity)) retained.VerifyBinding();
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0) Directory.Delete(folder, recursive: true);
            if (approvedLocation)
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager\" + id);
                Require(!Directory.Exists(folder) && key is null &&
                    !StorageProviderSyncRootManager.GetCurrentSyncRoots().Any(item => item.Id == id),
                    "Approved one-run fixture and exact test registration removed");
            }
        }
    }
    private static object? Invoke(string name, params object[] args) => typeof(ExplorerIntegration)
        .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args);
    private static Task InvokeAsync(string name, params object[] args) => (Task)Invoke(name, args)!;
    private static void Require(bool value, string label)
    { if (!value) throw new IOException(label); Console.WriteLine("PASS " + label); }

    private static void WriteRootMetadata(string folder, string stage)
    {
        var buffer = Marshal.AllocHGlobal(8192);
        try
        {
            var result = CfGetSyncRootInfoByPath(folder, 1, buffer, 8192, out var returned);
            Console.WriteLine($"NATIVE {stage} root-info result={result:X8} returned={returned}");
            if (result >= 0 && returned >= 1056)
                Console.WriteLine("NATIVE " + stage + " provider=" + Marshal.PtrToStringUni(buffer + 28) +
                    " population=" + Marshal.ReadInt16(buffer + 12) + " version=" + Marshal.PtrToStringUni(buffer + 540));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [DllImport("CldApi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CfGetSyncRootInfoByPath(string path, int infoClass, IntPtr buffer, int capacity, out int returned);
}

public class EmptyExplorerRemote : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        "VerifyRootIdentityAsync" => Task.CompletedTask,
        "ListEntriesAsync" => Task.FromResult<IReadOnlyList<PilotRemoteEntry>>([]),
        _ => throw new InvalidOperationException("Native registration must not issue a provider effect: " + method?.Name)
    };
}
