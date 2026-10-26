namespace Massdex.SyncShark.V2;

/// <summary>The kinds of potentially slow operation one run tracks; each resolves to a message prefix and a threshold.</summary>
internal enum RunOperationKind
{
    /// <summary>One provider HTTP request (and its body read); the label is the route path.</summary>
    Http,

    /// <summary>One upload/delete batch commit; the label is the route and the batch size.</summary>
    BatchCommit,

    /// <summary>One local file read or hash; the label is the item path.</summary>
    LocalIo,

    /// <summary>One local write, move or Recovery copy; the label is the item path.</summary>
    LocalWrite,

    /// <summary>One store call, its lock wait included; the label is the operation.</summary>
    Store,

    /// <summary>One semaphore or gate wait; the label is the gate.</summary>
    Gate,

    /// <summary>One provider token refresh; the label is the provider.</summary>
    TokenRefresh,

    TransferSlot,
}
