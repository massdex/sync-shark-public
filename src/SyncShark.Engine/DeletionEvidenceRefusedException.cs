namespace Massdex.SyncShark.Engine;

/// <summary>Deletion evidence refuses removal; ordinary diagnostic replay cannot supply that authority.</summary>
internal sealed class DeletionEvidenceRefusedException(string path) : ChangedUnderneathException(path)
{
}
