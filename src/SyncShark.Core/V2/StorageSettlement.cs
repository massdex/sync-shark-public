namespace Massdex.SyncShark.V2;

public sealed record StorageSettlement(long JobId, string RootId, string ObjectId, string RemoteBinding, PilotRemoteEntry Remote, long RefreshDue = 0, string? Kind = null)
{
    public const string AcknowledgeMove = "acknowledge-move";
}
