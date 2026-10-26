namespace Massdex.SyncShark.V2;

/// <summary>Observes a path's volume free space; null means it cannot be read.</summary>
public interface IVolumeSpaceProbe
{
    long? FreeBytes(string path);
}
