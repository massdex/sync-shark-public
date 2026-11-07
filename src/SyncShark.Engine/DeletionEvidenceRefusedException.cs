namespace Massdex.SyncShark.Engine;

/// <summary>Deletion evidence found the item elsewhere; ordinary diagnostic replay cannot authorize deletion.</summary>
internal sealed class DeletionEvidenceRefusedException(string path) : ChangedUnderneathException(path)
{
}
