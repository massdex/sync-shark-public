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
            try { L.VolumeSpace.Available(Path.Combine(dir.FullName,"missing")); throw new InvalidDataException("Absent directory reported capacity"); }
            catch(System.ComponentModel.Win32Exception) { }
        }
        finally { dir.Delete(true); }
        return Task.CompletedTask;
    }
}
