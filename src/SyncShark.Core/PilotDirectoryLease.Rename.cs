using System.Runtime.InteropServices;
using System.Text;

namespace Massdex.SyncShark;

public sealed partial class PilotDirectoryLease
{
    internal void RenameTo(PilotDirectoryLease destinationRoot, string destinationRelativePath)
    {
        if (!_canDelete)
            throw new InvalidOperationException("This directory lease has no rename authority.");
        VerifyBinding();
        destinationRoot.VerifyBinding();
        var relative = PilotPath.NormalizeRelative(destinationRelativePath);
        var destination = PilotPath.ResolveContainedPath(destinationRoot.Path, relative);
        if ((File.Exists(destination) || Directory.Exists(destination)) && !OccupantIsSelf(destination))
            throw new IOException("The directory move destination is occupied.");
        var separator = relative.LastIndexOf('/');
        PilotDirectoryLease? parent = null;
        try
        {
            if (separator >= 0)
                parent = OpenOrCreateDescendant(destinationRoot, relative[..separator]);
            var targetDirectory = parent ?? destinationRoot;
            var safeName = separator >= 0
                ? PilotPath.NormalizeTopLevelName(relative[(separator + 1)..])
                : PilotPath.NormalizeTopLevelName(relative);
            var destinationBytes = Encoding.Unicode.GetBytes(safeName);
            var rootOffset = IntPtr.Size == 8 ? 8 : 4;
            var lengthOffset = rootOffset + IntPtr.Size;
            var nameOffset = lengthOffset + sizeof(int);
            // FILE_RENAME_INFO ends in WCHAR FileName[1]. Its native structure size therefore
            // includes one WCHAR beyond the variable-name offset (and rounds to pointer
            // alignment on x64). Retain a zero WCHAR after FileName and pass that complete
            // buffer size; otherwise the directory name can acquire an uninitialized trailing
            // code unit on some Windows builds even though FileNameLength is exact.
            var bufferLength = checked(nameOffset + destinationBytes.Length + sizeof(char));
            var buffer = Marshal.AllocHGlobal(bufferLength);
            var destinationHandleAdded = false;
            try
            {
                for (var index = 0; index < bufferLength; index++) Marshal.WriteByte(buffer, index, 0);
                Marshal.WriteByte(buffer, 0, 0); // ReplaceIfExists = false.
                targetDirectory.Handle.DangerousAddRef(ref destinationHandleAdded);
                Marshal.WriteIntPtr(buffer, rootOffset, targetDirectory.Handle.DangerousGetHandle());
                Marshal.WriteInt32(buffer, lengthOffset, destinationBytes.Length);
                Marshal.Copy(destinationBytes, 0, buffer + nameOffset, destinationBytes.Length);
                var status = NtSetInformationFile(_handle, out _, buffer, checked((uint)bufferLength),
                    FileRenameInformation);
                if (status < 0)
                {
                    var error = unchecked((int)RtlNtStatusToDosError(status));
                    throw new IOException($"The exact held directory could not be renamed atomically (Windows error {error}).",
                        new System.ComponentModel.Win32Exception(error));
                }
            }
            finally
            {
                if (destinationHandleAdded) targetDirectory.Handle.DangerousRelease();
                Marshal.FreeHGlobal(buffer);
            }
            Path = destination;
            destinationRoot.VerifyBinding();
            parent?.VerifyBinding();
            if (!GetFileInformationByHandle(_handle, out var movedInformation))
                throw new IOException("The renamed directory identity became unreadable.");
            var movedIdentity = new PilotDirectoryIdentity(movedInformation.VolumeSerialNumber,
                ((ulong)movedInformation.FileIndexHigh << 32) | movedInformation.FileIndexLow);
            if (movedIdentity != Identity || IsUnsafeHeld(_handle, movedInformation.FileAttributes))
                throw new IOException("The renamed directory changed identity or became unsafe.");
            var observedPath = FinalPath(_handle);
            if (!string.Equals(observedPath, Path, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"The held directory rename path differs (expected '{System.IO.Path.GetFileName(Path)}', observed '{System.IO.Path.GetFileName(observedPath)}').");
            if (!PilotPath.HasExactRelativePath(destinationRoot.Path, observedPath, relative))
                throw new IOException("The exact held directory did not reach the intended case-sensitive membership.");
        }
        finally { parent?.Dispose(); }
    }

    private bool OccupantIsSelf(string destination)
    {
        // Case-equivalent names can still be distinct objects in case-sensitive NTFS
        // directories. Only the held source's exact identity may bypass occupancy.
        using var probe = CreateFile(destination, FileReadAttributes,
            FileShare.Read | FileShare.Write | FileShare.Delete, IntPtr.Zero,
            OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        return string.Equals(Path, destination, StringComparison.OrdinalIgnoreCase) && !probe.IsInvalid &&
            GetFileInformationByHandle(_handle, out var sourceInfo) &&
            GetFileInformationByHandle(probe, out var destinationInfo) &&
            sourceInfo.VolumeSerialNumber == destinationInfo.VolumeSerialNumber &&
            sourceInfo.FileIndexHigh == destinationInfo.FileIndexHigh &&
            sourceInfo.FileIndexLow == destinationInfo.FileIndexLow;
    }
}
