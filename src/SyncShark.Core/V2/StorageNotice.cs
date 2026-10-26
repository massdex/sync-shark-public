namespace Massdex.SyncShark.V2;

public sealed record StorageNotice(string RootId, string ObjectId, string RemoteBinding, string Operation, string? Scope = null);
