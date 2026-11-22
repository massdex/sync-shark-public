using L = Massdex.SyncShark.Engine.Local;
namespace Massdex.SyncShark.Engine.Tests.Local;

/// <summary>Checks the VolumeSpace adapter against observed outcomes.</summary>
public static class VolumeSpaceChecks
{
    public static Task RunAsync()
    {
        var dir=Directory.CreateTempSubdirectory();
        try
        {
            Check.Require(L.VolumeSpace.Available(dir.FullName)>0,"Expected caller-available space on the test volume");
            var deep = dir.FullName;
            while (deep.Length <= 300) deep = Path.Combine(deep, "ordinary-directory");
            var extended = @"\\?\" + deep;
            Check.Require(L.DriveIdentity.ToExtendedWindowsPath(deep)==extended && L.DriveIdentity.ToExtendedWindowsPath(extended)==extended, "Native long path still requires machine opt-in");
            Directory.CreateDirectory(extended);
            File.WriteAllText(Path.Combine(extended,"item"),"fixture");
            var expected = L.DriveIdentity.Read(extended).Identity;
            Check.Require(L.DriveIdentity.Read(deep).Identity == expected, "Deep root did not bind to the selected directory");
            var item = L.DirectoryListing.One(deep,"item");
            Check.Require(item is {Path:"item",Size:7} && item == L.DirectoryListing.One(extended,"item"), "Deep exact-name lookup missed the selected file");
            Check.Require(L.VolumeSpace.Available(deep)>0 && L.VolumeSpace.Available(extended)>0, "Deep destination did not report caller-available bytes");
            try { L.VolumeSpace.Available(Path.Combine(dir.FullName,"missing")); throw new InvalidDataException("Absent directory reported capacity"); }
            catch(System.ComponentModel.Win32Exception) { }
        }
        finally { dir.Delete(true); }
        return Task.CompletedTask;
    }
}
