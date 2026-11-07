using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

/// <summary>Owns native no-follow identities and exact installer file and mount-point effects.</summary>
// Windows PowerShell setup cannot load the application's .NET runtime.
public sealed class SetupFileSystem : IDisposable
{
    private const uint ReadAttributes = 0x80;
    private const uint DeleteAccess = 0x10000;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;
    private const uint MountPointTag = 0xA0000003;
    private const uint GetReparsePoint = 0x000900A8;
    private const uint SetReparsePoint = 0x000900A4;
    private const int MaximumReparseBytes = 16 * 1024; // MAXIMUM_REPARSE_DATA_BUFFER_SIZE, Windows SDK.
    private readonly SafeFileHandle handle;
    private readonly bool isDirectory;
    private readonly bool junction;
    public string Path { get; private set; }
    public string Identity { get; private set; }

    private SetupFileSystem(string path, bool isDirectory, bool writable, bool junction, bool mutable = false, bool claim = false)
    {
        Path = System.IO.Path.GetFullPath(path);
        this.isDirectory = isDirectory;
        this.junction = junction;
        uint access = ReadAttributes | GenericRead;
        if (mutable || junction || writable) access |= DeleteAccess;
        if (writable) access |= GenericWrite;
        // Windows opens a cross-directory rename target for write; never share delete.
        uint share = isDirectory && !junction ? 3U : 1U;
        handle = CreateFile(Path, access, claim ? 0U : share,
            IntPtr.Zero, claim ? 4U : 3U, OpenReparsePoint | BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(); }
        try
        {
            FileInformation info = Information();
            Identity = info.Volume.ToString("X8") + ":" + info.IndexHigh.ToString("X8") + info.IndexLow.ToString("X8");
            if (junction) ReadJunction();
        }
        catch { handle.Dispose(); throw; }
    }

    public static SetupFileSystem OpenDirectory(string path) { return new SetupFileSystem(path, true, false, false); }
    public static SetupFileSystem OpenMutableDirectory(string path) { return new SetupFileSystem(path, true, false, false, true); }
    public static SetupFileSystem OpenFile(string path) { return new SetupFileSystem(path, false, false, false); }
    public static SetupFileSystem OpenMutableFile(string path) { return new SetupFileSystem(path, false, false, false, true); }
    public static SetupFileSystem OpenJournal(string path) { return new SetupFileSystem(path, false, true, false); }
    public static SetupFileSystem OpenClaim(string path) { return new SetupFileSystem(path, false, true, false, false, true); }
    public static SetupFileSystem OpenJunction(string path) { return new SetupFileSystem(path, true, true, true); }

    public static bool Exists(string path)
    {
        try { File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    /// <summary>Proves that an alternate file path resolves to this held file.</summary>
    public void VerifyFilePath(string path)
    {
        // No-follow applies to the final file; opening it traverses the parent junction.
        using (var resolved = OpenFile(path))
        {
            if (!String.Equals(Identity, resolved.Identity, StringComparison.Ordinal))
                throw new InvalidDataException(Massdex.SyncShark.SetupMessages.Text("recovery.changed", path));
        }
    }

    public string Hash()
    {
        using (var borrowed = new SafeFileHandle(handle.DangerousGetHandle(), false))
        using (var stream = new FileStream(borrowed, FileAccess.Read))
        using (var hash = SHA256.Create())
        {
            stream.Position = 0;
            return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
        }
    }

    public byte[] Read()
    {
        using (var borrowed = new SafeFileHandle(handle.DangerousGetHandle(), false))
        using (var stream = new FileStream(borrowed, FileAccess.Read))
        using (var copy = new MemoryStream())
        {
            stream.Position = 0;
            stream.CopyTo(copy);
            return copy.ToArray();
        }
    }

    public void Append(byte[] bytes)
    {
        using (var borrowed = new SafeFileHandle(handle.DangerousGetHandle(), false))
        using (var stream = new FileStream(borrowed, FileAccess.ReadWrite))
        {
            stream.Seek(0, SeekOrigin.End);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }
    }

    public byte[] ReadJunction()
    {
        byte[] buffer = new byte[MaximumReparseBytes];
        uint count;
        if (!DeviceIoControl(handle, GetReparsePoint, null, 0, buffer, buffer.Length, out count, IntPtr.Zero))
            throw new Win32Exception();
        if (count < 16 || BitConverter.ToUInt32(buffer, 0) != MountPointTag ||
            BitConverter.ToUInt16(buffer, 4) + 8 != count) throw new InvalidDataException();
        Array.Resize(ref buffer, (int)count);
        return buffer;
    }

    public string JunctionTarget()
    {
        byte[] data = ReadJunction();
        int offset = BitConverter.ToUInt16(data, 8), length = BitConverter.ToUInt16(data, 10);
        if (16 + offset + length > data.Length || length % 2 != 0) throw new InvalidDataException();
        string target = Encoding.Unicode.GetString(data, 16 + offset, length);
        if (!target.StartsWith(@"\??\", StringComparison.Ordinal)) throw new InvalidDataException();
        return System.IO.Path.GetFullPath(target.Substring(4));
    }

    // Microsoft documents modification of an existing reparse point with the same ReparseTag:
    // https://learn.microsoft.com/windows-hardware/drivers/ifs/fsctl-set-reparse-point
    // No delete/rename pair: readers always have an active junction name.
    public void Retarget(byte[] expected, byte[] replacement)
    {
        if (!Equal(ReadJunction(), expected) || BitConverter.ToUInt32(replacement, 0) != MountPointTag)
            throw new InvalidDataException();
        uint count;
        if (!DeviceIoControl(handle, SetReparsePoint, replacement, replacement.Length, null, 0, out count, IntPtr.Zero))
            throw new Win32Exception();
        if (!Equal(ReadJunction(), replacement)) throw new IOException();
    }

    public static byte[] JunctionData(string target)
    {
        target = System.IO.Path.GetFullPath(target);
        byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
        byte[] print = Encoding.Unicode.GetBytes(target);
        int size = 16 + substitute.Length + 2 + print.Length + 2;
        if (size > MaximumReparseBytes) throw new PathTooLongException();
        byte[] data = new byte[size];
        Array.Copy(BitConverter.GetBytes(MountPointTag), 0, data, 0, 4);
        Array.Copy(BitConverter.GetBytes((ushort)(size - 8)), 0, data, 4, 2);
        Array.Copy(BitConverter.GetBytes((ushort)substitute.Length), 0, data, 10, 2);
        Array.Copy(BitConverter.GetBytes((ushort)(substitute.Length + 2)), 0, data, 12, 2);
        Array.Copy(BitConverter.GetBytes((ushort)print.Length), 0, data, 14, 2);
        Array.Copy(substitute, 0, data, 16, substitute.Length);
        Array.Copy(print, 0, data, 18 + substitute.Length, print.Length);
        return data;
    }

    public void Rename(string target)
    {
        Verify();
        target = System.IO.Path.GetFullPath(target);
        byte[] name = Encoding.Unicode.GetBytes(target);
        int nameOffset = IntPtr.Size == 8 ? 20 : 12;
        int bufferSize = nameOffset + name.Length + sizeof(char);
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            Marshal.Copy(new byte[nameOffset], 0, buffer, nameOffset);
            Marshal.WriteInt32(buffer, nameOffset - 4, name.Length);
            Marshal.Copy(name, 0, IntPtr.Add(buffer, nameOffset), name.Length);
            // FILE_RENAME_INFO.FileName is NUL-terminated; FileNameLength excludes that WCHAR.
            Marshal.WriteInt16(buffer, nameOffset + name.Length, 0);
            if (!SetFileInformationByHandle(handle, 3, buffer, (uint)bufferSize)) throw new Win32Exception();
            Path = target;
            Verify();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    // Delete only the held object; a foreign child makes directory deletion fail, never recurse.
    public void Delete()
    {
        IntPtr buffer = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(buffer, 1);
            if (!SetFileInformationByHandle(handle, 4, buffer, 4)) throw new Win32Exception();
        }
        finally { Marshal.FreeHGlobal(buffer); }
        Dispose();
    }

    public static bool Equal(byte[] left, byte[] right)
    {
        if (left.Length != right.Length) return false;
        for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
        return true;
    }

    /// <summary>Revalidates the original object kind through its held no-follow handle.</summary>
    public void Verify() { Information(); }

    private FileInformation Information()
    {
        FileInformation result;
        if (!GetFileInformationByHandle(handle, out result)) throw new Win32Exception();
        if (((result.Attributes & 0x10) != 0) != isDirectory ||
            ((result.Attributes & 0x400) != 0) != junction ||
            (!isDirectory && result.Links != 1) || (result.Attributes & 0x1000) != 0)
            throw new InvalidDataException(Massdex.SyncShark.SetupMessages.Text("object.invalid", Path));
        return result;
    }

    public void Dispose() { handle.Dispose(); }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle file, uint code, byte[] input, int inputLength, byte[] output, int outputLength, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, IntPtr information, uint size);
}
