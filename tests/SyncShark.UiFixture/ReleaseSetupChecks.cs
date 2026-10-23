using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Massdex.SyncShark;

namespace SyncShark.UiFixture;

internal static class ReleaseSetupChecks
{
    internal static int Run()
    {
        var count = 0;
        void Check(bool value, string label)
        { if (!value) throw new InvalidOperationException(label); count++; Console.WriteLine("PASS " + label); }
        void Reject(Action action, string label)
        { try { action(); } catch (Exception error) when (error is IOException or ArgumentException or System.Xml.XmlException) { Check(true, label); return; } throw new InvalidOperationException("Expected rejection: " + label); }
        foreach (var mode in new[] { "Configure", "Restore", "Remove" })
        {
            var start = MainWindow.BuildDiscoverySetupStart(mode, @"C:\Fixture\A's 名\AppData\Local\Massdex\SyncShark\versions\0.3.19", 123);
            Check(Path.GetFileName(start.FileName) == "SyncSharkDiscovery.exe" && start.UseShellExecute && start.Verb == "runas",
                mode + " elevates only the fixed native product executable");
            Check(start.ArgumentList.SequenceEqual(new[] { mode, "123" }), mode + " passes only a fixed verb and PID; no script or caller-selected destination");
        }
        Reject(() => MainWindow.BuildDiscoverySetupStart("Arbitrary", @"C:\", 1), "Unknown setup mode is rejected");
        Reject(() => MainWindow.BuildDiscoverySetupStart("Configure", @"C:\", 0), "Missing requesting process is rejected");
        Check(MainWindow.DiscoverySetupFailureText(0x20110005).Contains("Registering the Windows Task (Error 0x0005)", StringComparison.Ordinal),
            "A protected setup failure identifies its exact stage and numeric error");
        Check(MainWindow.DiscoverySetupFailureText(0x20070542).Contains("Opening the Setup Transaction (Error 0x0542)", StringComparison.Ordinal),
            "Earlier setup failures retain their own phase instead of a generic failure");
        Check(MainWindow.DiscoverySetupFailureText(1).Contains("Error 0x00000001", StringComparison.Ordinal),
            "Native bootstrap failures preserve their numeric exit code");
        Check(MainWindow.DiscoverySetupFailureText(0x20ff0005).Contains("Error 0x20FF0005", StringComparison.Ordinal),
            "An unknown phase cannot index outside the fixed safe labels");
        var readySummary = MainWindow.DiscoveryCheckSummary(new("Pair A", true, 12,
            TimeSpan.FromSeconds(1), "Fast Discovery Is Ready"));
        Check(readySummary.Contains("Folder Pair: Pair A\n", StringComparison.Ordinal) &&
            readySummary.Contains("\nMetadata Entries: 12\nDuration:", StringComparison.Ordinal) && !readySummary.Contains('·'),
            "Discovery check gives pair, metadata count and duration separate labelled lines");
        var unavailableSummary = MainWindow.DiscoveryCheckSummary(new("Pair A", false, 0,
            TimeSpan.Zero, "Not Set Up; Ordinary Scanning Is Active"));
        Check(unavailableSummary.Contains("Ordinary Scanning", StringComparison.Ordinal) &&
            !unavailableSummary.Contains("Metadata Entries:", StringComparison.Ordinal),
            "Unavailable Discovery does not present zero entries as a successful scan");
        // Use the real split-token boundary, without impersonating, elevating or
        // acting as the linked identity. Before the fix MediumToken throws1346.
        var linkedIdentity = SyncSharkDiscoverySetupNative.MediumToken();
        try
        {
            Check(GetTokenInformation(linkedIdentity, 9, out var level, sizeof(int), out _) && level == 1,
                "Actual linked-token ACL identity remains SecurityIdentification without a forbidden upgrade");
            Check(GetTokenInformation(linkedIdentity, 8, out var kind, sizeof(int), out _) && kind == 2,
                "ACL checking receives a query-only impersonation-token identity, not a primary execution token");
        }
        finally { SyncSharkDiscoverySetupNative.CloseToken(linkedIdentity); }
        var nativeHelper = Path.Combine(Path.GetDirectoryName(typeof(App).Assembly.Location)!, "SyncSharkDiscovery.exe");
        var helperResources = LoadLibraryEx(nativeHelper, IntPtr.Zero, 0x22); // data/image resources; no code execution
        Check(helperResources != IntPtr.Zero, "Open the built helper only as image resources");
        try
        {
            var group = FindResource(helperResources, new IntPtr(1), new IntPtr(14));
            Check(group != IntPtr.Zero, "The Discovery elevation executable contains its primary application icon group");
            var data = LockResource(LoadResource(helperResources, group));
            var size = SizeofResource(helperResources, group);
            Check(data != IntPtr.Zero && size >= 20 && Marshal.ReadInt16(data, 2) == 1 && Marshal.ReadInt16(data, 4) > 0,
                "The helper icon group has valid Windows icon entries");
            var entries = (ushort)Marshal.ReadInt16(data, 4);
            Check(size == 6 + entries * 14, "The helper icon directory is complete");
            for (var entry = 0; entry < entries; entry++)
            {
                var offset = 6 + entry * 14;
                var resource = FindResource(helperResources, new IntPtr((ushort)Marshal.ReadInt16(data, offset + 12)), new IntPtr(3));
                Check(resource != IntPtr.Zero && SizeofResource(helperResources, resource) == (uint)Marshal.ReadInt32(data, offset + 8),
                    "Helper icon image " + (entry + 1) + " is embedded and complete");
            }
        }
        finally { FreeLibrary(helperResources); }
        var product = SyncSharkRuntimePaths.ProductRoot;
        var activeExecutable = Path.Combine(product, "active", "SyncShark.exe");
        var resolvedExecutable = ExistingWindowActivation.ResolveExecutable(activeExecutable);
        Check(!resolvedExecutable.StartsWith(@"\\?\", StringComparison.Ordinal) && Path.IsPathFullyQualified(resolvedExecutable),
            "Actual installed executable resolves to the canonical local DOS path, without a device prefix");
        if (SyncSharkRuntimePaths.IsInstalledRuntimeDirectory(Path.GetDirectoryName(resolvedExecutable)!))
        {
            using var installed = PilotDirectoryLease.OpenInstalledRuntime(Path.GetDirectoryName(resolvedExecutable)!);
            installed.VerifyBinding();
            Check(Path.GetDirectoryName(installed.Path)!.Equals(SyncSharkRuntimePaths.InstalledVersionsRoot, StringComparison.OrdinalIgnoreCase),
                "Handle-resolved active executable passes the exact installed-runtime lease without admitting the active junction");
        }
        else Reject(() => PilotDirectoryLease.OpenInstalledRuntime(Path.GetDirectoryName(resolvedExecutable)!),
            "Actual writable predecessor is not admitted as a protected runtime (positive installed successor proof remains separate)");
        Reject(() => PilotDirectoryLease.OpenInstalledRuntime(product), "Runtime lease cannot target product state");
        Reject(() => PilotDirectoryLease.OpenInstalledRuntime(Path.Combine(product, "active")), "Runtime lease cannot accept the mutable active junction");
        Reject(() => PilotDirectoryLease.OpenInstalledRuntime(Path.Combine(product, "versions", "0.3.18", "nested")), "Runtime lease cannot target an arbitrary descendant");

        var maintenanceRoot = Path.Combine(Path.GetTempPath(), "sync-shark-ui-inspection", Guid.NewGuid().ToString("N"));
        var maintenanceState = Path.Combine(maintenanceRoot, "state");
        Directory.CreateDirectory(maintenanceState);
        var maintenanceLock = Path.Combine(maintenanceState, "instance.lock");
        try
        {
            File.WriteAllText(maintenanceLock, "31415", new UTF8Encoding(false));
            var beforeLock = File.ReadAllBytes(maintenanceLock);
            using (var maintenance = PilotInstanceLock.AcquireForMaintenance(maintenanceLock))
            {
                maintenance.StateAuthority.VerifyAuthority();
                Reject(() => PilotInstanceLock.Acquire(maintenanceLock), "Maintenance lock still excludes a normal product instance");
                Reject(() => PilotInstanceLock.AcquireForMaintenance(maintenanceLock), "Maintenance lock excludes another maintenance operation");
            }
            Check(File.ReadAllBytes(maintenanceLock).SequenceEqual(beforeLock), "Maintenance leaves existing process-lock bytes exactly unchanged");
            using (var normal = PilotInstanceLock.Acquire(maintenanceLock)) normal.StateAuthority.VerifyAuthority();
            Check(File.ReadAllText(maintenanceLock) == Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "Normal product launch still records its process ID after maintenance releases the lock");
        }
        finally { Directory.Delete(maintenanceRoot, recursive: true); }

        var version = typeof(App).Assembly.GetName().Version!;
        var expectedDirectory = Path.Combine(product, "versions", "0.3.21");
        Check(ExplorerPackage.MatchesRegistration(version, expectedDirectory, version, expectedDirectory),
            "An exact existing registration is eligible for payload verification instead of reinstalling itself");
        Check(ExplorerPackage.MatchesRegistration(version, expectedDirectory, version, expectedDirectory.ToUpperInvariant() + "\\"),
            "Equivalent Windows directory case and trailing separators do not request a package move");
        Check(!ExplorerPackage.MatchesRegistration(version, expectedDirectory, new Version(0, 3, 20, 0), expectedDirectory),
            "A predecessor version cannot pass the idempotent-registration decision");
        Check(!ExplorerPackage.MatchesRegistration(version, expectedDirectory, version, expectedDirectory + "-other"),
            "An adjacent external runtime cannot pass registration readback");
        Check(!ExplorerPackage.MatchesRegistration(version, expectedDirectory, version, string.Empty),
            "A missing external location cannot pass registration readback");
        using (var valid = Package(version)) { ExplorerPackage.Validate(valid, version); Check(true, "Exact sparse-package structural identity is accepted; this is not signature acceptance"); }
        using (var wrong = Package(new Version(version.Major, version.Minor, version.Build + 1, 0)))
            Reject(() => ExplorerPackage.Validate(wrong, version), "A package for another app version is rejected");
        using (var wrong = Package(version, publisher: "CN=Other"))
            Reject(() => ExplorerPackage.Validate(wrong, version), "Another package publisher is rejected");
        using (var wrong = Package(version, executable: "other.exe"))
            Reject(() => ExplorerPackage.Validate(wrong, version), "Another package executable is rejected");
        using (var unsigned = Package(version, signature: false))
            Reject(() => ExplorerPackage.Validate(unsigned, version), "Missing package signature container is rejected");
        using (var duplicate = Package(version, extra: "AppxManifest.xml"))
            Reject(() => ExplorerPackage.Validate(duplicate, version), "Duplicate package members are rejected");
        using (var extra = Package(version, extra: "other.dll"))
            Reject(() => ExplorerPackage.Validate(extra, version), "Unapproved executable package payload is rejected");
        using (var missing = Package(version, command: false))
            Reject(() => ExplorerPackage.Validate(missing, version), "An identity-only sparse package cannot register an external COM payload");
        using (var outside = Package(version, serverPath: @"..\external.dll"))
            Reject(() => ExplorerPackage.Validate(outside, version), "A COM class pointing outside the packaged member is rejected");
        using (var external = Package(version, serverRuntime: "win32App"))
            Reject(() => ExplorerPackage.Validate(external, version), "The obsolete win32App COM manifest declaration is rejected; this is not loaded-path proof");
        using (var inherited = Package(version, serverRuntime: null))
            Reject(() => ExplorerPackage.Validate(inherited, version), "A COM manifest missing the explicit runtime declaration is rejected");
        using (var sandbox = Package(version, serverTrust: "appContainer"))
            Reject(() => ExplorerPackage.Validate(sandbox, version), "An unexpected COM trust level is rejected");
        Console.WriteLine(count + " setup/package structure checks passed; no windows, elevation, registration or product-state mutation.");
        return 0;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int kind, out int value, int length, out int required);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32.dll")]
    private static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")]
    private static extern bool FreeLibrary(IntPtr module);

    private static MemoryStream Package(Version version, string? publisher = null, string executable = "SyncShark.exe", bool signature = true, string? extra = null, bool command = true, string serverPath = "SyncShark.Explorer.dll", string? serverRuntime = "packagedClassicApp", string serverTrust = "mediumIL")
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("AppxManifest.xml").Open()))
                writer.Write($"<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\" xmlns:com=\"http://schemas.microsoft.com/appx/manifest/com/windows10\" xmlns:uap10=\"http://schemas.microsoft.com/appx/manifest/uap/windows10/10\"><Identity Name=\"{ExplorerPackage.Name}\" Publisher=\"{publisher ?? ExplorerPackage.Publisher}\" Version=\"{version}\" ProcessorArchitecture=\"x64\"/><Applications><Application Id=\"SyncShark\" Executable=\"{executable}\" uap10:RuntimeBehavior=\"win32App\"><Extensions><com:Extension Category=\"windows.comServer\" {(serverRuntime is null ? "" : $"uap10:RuntimeBehavior=\"{serverRuntime}\"")} uap10:TrustLevel=\"{serverTrust}\"><com:ComServer><com:SurrogateServer><com:Class Id=\"B2251D9A-7803-4F1F-8FE8-CBDA284BD5E9\" Path=\"{serverPath}\" ThreadingModel=\"STA\"/></com:SurrogateServer></com:ComServer></com:Extension></Extensions></Application></Applications></Package>");
            if (command) { using var content = zip.CreateEntry("SyncShark.Explorer.dll").Open(); content.Write("MZfixture"u8); }
            if (signature) { using var content = zip.CreateEntry("AppxSignature.p7x").Open(); content.Write("PKCXfixture"u8); }
            if (extra is not null) { using var content = zip.CreateEntry(extra).Open(); content.WriteByte(1); }
        }
        stream.Position = 0; return stream;
    }
}
