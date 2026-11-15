using System.ComponentModel;
using System.Diagnostics;
using Massdex.SyncShark.Engine.Local;

namespace Massdex.SyncShark.Engine.Tests.Local;

/// <summary>Checks for VolumeSpace's destination-parent volume serial and caller-available space
/// (`--engine-checks local`), written by VolumeSpace's writer.</summary>
public static class VolumeSpaceChecks
{
    public static void Run()
    {
        var root = Directory.CreateTempSubdirectory(nameof(VolumeSpaceChecks));
        try
        {
            var serial = SameVolume(root.FullName);
            FileIsNotAParent(root.FullName, serial);
            MissingParent(root.FullName, serial);
            SubstAlias(root.FullName, serial);
            LongPath(root.FullName, serial);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    // The serial is the destination directory's own volume, whatever spelling reaches it.
    private static uint SameVolume(string root)
    {
        var serial = VolumeSpace.Serial(root);
        Check.Require(VolumeSpace.Serial(Path.GetPathRoot(root)!) == serial,
            "The serial is read from the destination directory's own volume.");
        Check.Require(VolumeSpace.Serial(root + Path.DirectorySeparatorChar) == serial,
            "A trailing separator names the same parent volume.");
        Check.Require(VolumeSpace.Serial(@"\\?\" + root) == serial,
            "The shared extended spelling reaches the same parent volume.");
        Check.Require(VolumeSpace.Available(root) > 0 &&
            VolumeSpace.Available(root + Path.DirectorySeparatorChar) > 0,
            "Caller-available space reads on the destination directory.");
        return serial;
    }

    // A file cannot be the destination parent, and that refusal is not a native observation error.
    private static void FileIsNotAParent(string root, uint serial)
    {
        var file = Path.Combine(root, "Item.txt");
        File.WriteAllText(file, "");
        Check.Require(Throws<IOException>(() => VolumeSpace.Serial(file)),
            "A file is refused as a destination parent.");
        Check.Require(VolumeSpace.Serial(root) == serial, "A valid observation still works after the file refusal.");
    }

    // A missing parent keeps its native not-found code so the caller can map it to ParentMissing.
    private static void MissingParent(string root, uint serial)
    {
        var missing = Path.Combine(root, "Missing");
        Check.Require(NativeCode(() => VolumeSpace.Serial(missing)) is 2 or 3,
            "A missing parent's serial failure keeps ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND.");
        Check.Require(NativeCode(() => VolumeSpace.Available(missing)) is 2 or 3,
            "A missing parent's caller-space failure keeps the same not-found code.");
        Check.Require(VolumeSpace.Serial(root) == serial && VolumeSpace.Available(root) > 0,
            "A later observation still works after a not-found refusal.");
    }

    // A subst alias reports the underlying volume serial, so physical aliases share one accounting pool.
    private static void SubstAlias(string root, uint serial)
    {
        var drive = UnusedDriveLetter();
        if (drive is null || !Subst(drive, root))
        {
            Check.Skip();
            return;
        }
        try
        {
            var alias = drive + Path.DirectorySeparatorChar;
            Check.Require(VolumeSpace.Serial(alias) == serial,
                "A subst alias reports the destination's own volume serial.");
            Check.Require(VolumeSpace.Available(alias) > 0,
                "Caller-available space reads through a subst alias, without a volume GUID.");
        }
        finally
        {
            Check.Require(Subst(drive, "/D") && !IsDriveInUse(drive), "The owned subst mapping is removed.");
        }
    }

    // A path beyond the legacy limit reaches the same volume through the shared extended spelling.
    private static void LongPath(string root, uint serial)
    {
        var path = root;
        for (var level = 0; level < 8; level++) path = Path.Combine(path, new string('a', 40));
        var extended = @"\\?\" + path;
        Directory.CreateDirectory(extended);
        try
        {
            Check.Require(VolumeSpace.Serial(path) == serial && VolumeSpace.Available(path) > 0,
                "A path beyond the legacy limit reaches the same volume.");
        }
        finally
        {
            Directory.Delete(extended, true);
        }
    }

    private static string? UnusedDriveLetter()
    {
        var used = DriveInfo.GetDrives().Select(info => info.Name[0]).ToHashSet();
        for (var letter = 'Z'; letter >= 'Q'; letter--)
            if (!used.Contains(letter)) return letter + ":";
        return null;
    }

    private static bool IsDriveInUse(string drive) =>
        DriveInfo.GetDrives().Any(info =>
            string.Equals(info.Name, drive + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    private static bool Subst(string drive, string target)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "subst.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(drive);
        start.ArgumentList.Add(target);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (T)
        {
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static int? NativeCode(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Win32Exception error)
        {
            return error.NativeErrorCode;
        }
    }
}
