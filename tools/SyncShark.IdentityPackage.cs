using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Xml;

// Small build-only adapter over the Windows packaging API. It does not register a
// package, install trust, select a certificate, or execute an external build tool.
public static class SyncSharkIdentityPackage
{
    [StructLayout(LayoutKind.Sequential)] private struct Settings { public int ForceZip32; public IntPtr HashMethod; }
    [ComImport, Guid("beb94909-e451-438b-b5a7-d79e767b75d8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface Factory
    {
        void CreatePackageWriter(IStream output, ref Settings settings, out Writer writer);
        void CreatePackageReader(IStream input, [MarshalAs(UnmanagedType.Interface)] out object reader);
    }
    [ComImport, Guid("9099e33b-246f-41e4-881a-008eb613f858"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface Writer
    {
        void AddPayloadFile([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string contentType, int compression, IStream input);
        void Close(IStream manifest);
    }
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateStreamOnFileEx(string path, uint mode, uint attributes, [MarshalAs(UnmanagedType.Bool)] bool create, IntPtr template, out IStream stream);
    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern void CreateStreamOnHGlobal(IntPtr memory, [MarshalAs(UnmanagedType.Bool)] bool deleteOnRelease, out IStream stream);
    [DllImport("urlmon.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void CreateUri(string value, uint flags, UIntPtr reserved, out IntPtr uri);
    public static void Create(string output, string logo, string explorer, string version)
    {
        Create(output, logo, explorer, version, null);
    }
    public static void Create(string output, string logo, string explorer, string version, string shellResourceRoot,
        string ownerAllocation = null)
    {
        Version parsed;
        if (!Version.TryParse(version, out parsed) || parsed.Build < 0 || parsed.Revision < 0 ||
            parsed.Major > 65535 || parsed.Minor > 65535 || parsed.Build > 65535 || parsed.Revision > 65535)
            throw new ArgumentException("A four-part package version is required.");
        output = Path.GetFullPath(output); logo = Path.GetFullPath(logo); explorer = Path.GetFullPath(explorer);
        string allocation = OwnerAllocation(ownerAllocation);
        if (!output.StartsWith(allocation, StringComparison.OrdinalIgnoreCase) || Path.GetExtension(output) != ".msix")
            throw new InvalidOperationException("Package construction is confined to the existing owner build allocation.");
        if (File.Exists(output)) throw new IOException("Use a fresh output or clean the prior owner build artifact first.");
        if (!File.Exists(logo) || new FileInfo(logo).Length > 1024 * 1024) throw new InvalidDataException("A bounded product logo is required.");
        if (!explorer.StartsWith(allocation, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(explorer) != "SyncShark.Explorer.dll" ||
            !File.Exists(explorer) || new FileInfo(explorer).Length < 1 || new FileInfo(explorer).Length > 1024 * 1024)
            throw new InvalidDataException("The exact bounded Explorer build payload is required.");
        for (var entry = new FileInfo(explorer) as FileSystemInfo; entry != null; entry = entry is FileInfo ? ((FileInfo)entry).Directory : ((DirectoryInfo)entry).Parent)
            if ((entry.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0) throw new IOException("Explorer payload cannot cross a reparse or offline item.");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(output)); parent != null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Package output cannot cross a reparse point.");
        // Release preparation requires this overload and a real generated PRI.
        // The legacy overload remains for old contained-COM construction checks;
        // it is not sufficient proof of unplated shell resource selection.
        string[] shellMembers = shellResourceRoot == null ? new string[0] :
            ValidateShellResources(shellResourceRoot, logo, ownerAllocation);
        string xml = Manifest(parsed.ToString());
        Factory factory = null; Writer writer = null; IStream target = null, picture = null, command = null, manifest = null, readback = null; object reader = null;
        IntPtr hash = IntPtr.Zero; string phase = "Create Windows packaging factory";
        try
        {
            factory = (Factory)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("5842a140-ff9f-4166-8f5c-62f5b7b0c781"), true));
            SHCreateStreamOnFileEx(output, 0x00001012, 0x80, true, IntPtr.Zero, out target);
            phase = "Create SHA-256 URI";
            CreateUri("http://www.w3.org/2001/04/xmlenc#sha256", 0x100, UIntPtr.Zero, out hash);
            var settings = new Settings { ForceZip32 = 1, HashMethod = hash };
            phase = "Create package writer";
            factory.CreatePackageWriter(target, ref settings, out writer);
            phase = "Add product logo";
            SHCreateStreamOnFileEx(logo, 0x20, 0x80, false, IntPtr.Zero, out picture);
            writer.AddPayloadFile("Assets\\SyncShark-64.png", "image/png", 1, picture);
            foreach (string name in shellMembers)
            {
                IStream resource = null;
                try
                {
                    phase = "Add shell resource " + name;
                    SHCreateStreamOnFileEx(Path.Combine(shellResourceRoot, name), 0x20, 0x80, false, IntPtr.Zero, out resource);
                    writer.AddPayloadFile(name, name.EndsWith(".png", StringComparison.Ordinal) ? "image/png" : "application/octet-stream", 1, resource);
                }
                finally { if (resource != null) Marshal.FinalReleaseComObject(resource); }
            }
            // Contained bytes alone do not prove COM load resolution: the actual
            // installed module path must pass the protected-loading acceptance.
            // Release preparation repacks the final signed DLL before MSIX signing.
            phase = "Add protected Explorer command";
            SHCreateStreamOnFileEx(explorer, 0x20, 0x80, false, IntPtr.Zero, out command);
            writer.AddPayloadFile("SyncShark.Explorer.dll", "application/octet-stream", 1, command);
            CreateStreamOnHGlobal(IntPtr.Zero, true, out manifest);
            var bytes = new UTF8Encoding(false).GetBytes(xml); manifest.Write(bytes, bytes.Length, IntPtr.Zero); manifest.Seek(0, 0, IntPtr.Zero);
            phase = "Finalize package manifest";
            writer.Close(manifest); target.Commit(0);
            Marshal.FinalReleaseComObject(writer); writer = null; Marshal.FinalReleaseComObject(target); target = null;
            SHCreateStreamOnFileEx(output, 0x20, 0x80, false, IntPtr.Zero, out readback);
            phase = "Read completed package"; factory.CreatePackageReader(readback, out reader);
        }
        catch (Exception error) { throw new IOException(phase + " failed (0x" + error.HResult.ToString("X8") + "): " + error.Message, error); }
        finally
        {
            foreach (object item in new object[] { reader, readback, manifest, picture, command, writer, target, factory })
                if (item != null && Marshal.IsComObject(item)) Marshal.FinalReleaseComObject(item);
            if (hash != IntPtr.Zero) Marshal.Release(hash);
        }
    }
    public static string[] ShellAssetNames()
    {
        var names = new System.Collections.Generic.List<string>();
        // Existing transparent artwork covers the common Windows target sizes.
        // Both theme variants are needed even when their pixels are identical.
        foreach (int size in new int[] { 16, 20, 24, 32, 48, 64, 256 })
            foreach (string form in new string[] { "", "_altform-unplated", "_altform-lightunplated" })
                names.Add("Assets\\SyncShark-64.targetsize-" + size + form + ".png");
        return names.ToArray();
    }
    // Migration-owned reference repair: the owner build allocation may be supplied explicitly by the caller; the
    // legacy LocalAppData allocation remains the default so the existing preimage is unchanged.
    private static string OwnerAllocation(string ownerAllocation)
    {
        string value = string.IsNullOrWhiteSpace(ownerAllocation)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SyncShark", "build")
            : Path.GetFullPath(ownerAllocation);
        return value.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }
    public static string[] ValidateShellResources(string root, string logo, string ownerAllocation = null)
    {
        root = Path.GetFullPath(root);
        string allocation = OwnerAllocation(ownerAllocation);
        if (!root.StartsWith(allocation, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(root))
            throw new InvalidDataException("Shell resources must be in the existing owner build allocation.");
        var members = new System.Collections.Generic.List<string>(ShellAssetNames());
        members.Add("resources.pri");
        var checkedMembers = new System.Collections.Generic.List<string>(members);
        checkedMembers.Add("Assets\\SyncShark-64.png");
        foreach (string name in checkedMembers)
        {
            string path = Path.Combine(root, name);
            if (!File.Exists(path) || new FileInfo(path).Length < 1 || new FileInfo(path).Length > 4 * 1024 * 1024)
                throw new InvalidDataException("Missing or oversized shell resource: " + name);
            for (FileSystemInfo entry = new FileInfo(path); entry != null; entry = entry is FileInfo ? ((FileInfo)entry).Directory : ((DirectoryInfo)entry).Parent)
                if ((entry.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
                    throw new IOException("Shell resources cannot cross a reparse or offline item.");
        }
        // The index's unqualified image must bind the actual package image, not
        // a different staging icon. PRI generation/selection has its own proof.
        string baseImage = Path.Combine(root, "Assets", "SyncShark-64.png");
        using (var algorithm = System.Security.Cryptography.SHA256.Create())
        using (var expected = File.OpenRead(logo))
        using (var actual = File.OpenRead(baseImage))
            if (Convert.ToBase64String(algorithm.ComputeHash(expected)) != Convert.ToBase64String(algorithm.ComputeHash(actual)))
                throw new InvalidDataException("Indexed base icon differs from the package icon.");
        return members.ToArray();
    }
    public static string Manifest(string version)
    {
        // Class, application and package identity are registered
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\" " +
            "xmlns:uap=\"http://schemas.microsoft.com/appx/manifest/uap/windows10\" xmlns:uap10=\"http://schemas.microsoft.com/appx/manifest/uap/windows10/10\" " +
            "xmlns:desktop4=\"http://schemas.microsoft.com/appx/manifest/desktop/windows10/4\" xmlns:desktop5=\"http://schemas.microsoft.com/appx/manifest/desktop/windows10/5\" " +
            "xmlns:com=\"http://schemas.microsoft.com/appx/manifest/com/windows10\" xmlns:rescap=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities\" IgnorableNamespaces=\"uap uap10 desktop4 desktop5 com rescap\">" +
            "<Identity Name=\"Massdex.SyncShark\" Publisher=\"CN=Massdex Sync Shark Code Signing v1, O=Massdex\" Version=\"" + version + "\" ProcessorArchitecture=\"x64\"/>" +
            "<Properties><DisplayName>Sync Shark</DisplayName><PublisherDisplayName>Massdex</PublisherDisplayName><Logo>Assets\\SyncShark-64.png</Logo><uap10:AllowExternalContent>true</uap10:AllowExternalContent></Properties>" +
            "<Resources><Resource Language=\"en-us\"/></Resources><Dependencies><TargetDeviceFamily Name=\"Windows.Desktop\" MinVersion=\"10.0.19041.0\" MaxVersionTested=\"10.0.26100.0\"/></Dependencies>" +
            "<Applications><Application Id=\"SyncShark\" Executable=\"SyncShark.exe\" uap10:TrustLevel=\"mediumIL\" uap10:RuntimeBehavior=\"win32App\">" +
            "<uap:VisualElements AppListEntry=\"none\" DisplayName=\"Sync Shark\" Description=\"Sync Shark Explorer integration\" BackgroundColor=\"transparent\" Square150x150Logo=\"Assets\\SyncShark-64.png\" Square44x44Logo=\"Assets\\SyncShark-64.png\"/>" +
            // Explicit COM runtime metadata is retained, but actual signed30
            // still resolved the DLL externally. SEC003 remains open until the
            // separately authorized loading correction passes installed proof.
            "<Extensions><com:Extension Category=\"windows.comServer\" uap10:RuntimeBehavior=\"packagedClassicApp\" uap10:TrustLevel=\"mediumIL\"><com:ComServer><com:SurrogateServer DisplayName=\"Sync Shark\"><com:Class Id=\"B2251D9A-7803-4F1F-8FE8-CBDA284BD5E9\" Path=\"SyncShark.Explorer.dll\" ThreadingModel=\"STA\"/></com:SurrogateServer></com:ComServer></com:Extension>" +
            "<desktop4:Extension Category=\"windows.fileExplorerContextMenus\"><desktop4:FileExplorerContextMenus>" +
            "<desktop5:ItemType Type=\"*\"><desktop5:Verb Id=\"SyncShark\" Clsid=\"B2251D9A-7803-4F1F-8FE8-CBDA284BD5E9\"/></desktop5:ItemType>" +
            "<desktop5:ItemType Type=\"Directory\"><desktop5:Verb Id=\"SyncShark\" Clsid=\"B2251D9A-7803-4F1F-8FE8-CBDA284BD5E9\"/></desktop5:ItemType>" +
            "</desktop4:FileExplorerContextMenus></desktop4:Extension></Extensions></Application></Applications>" +
            "<Capabilities><rescap:Capability Name=\"runFullTrust\"/><rescap:Capability Name=\"unvirtualizedResources\"/></Capabilities></Package>";
    }
}
