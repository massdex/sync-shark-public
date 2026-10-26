namespace Massdex.SyncShark.V2;

/// <summary>
/// The writer's own byte admission refused after the durable intent was recorded (the up-front admission could
/// reserve metadata only). The executor maps it to the same pending free-space notice as an up-front refusal.
/// Throw only before any effect: the executor records this as a cleanly pending (Skipped) outcome, which must never
/// hide a partial write.
/// </summary>
public sealed class FreeSpaceRefusedException(FreeSpaceRefusal refusal) : IOException(refusal.Detail)
{
    public FreeSpaceRefusal Refusal { get; } = refusal;
}
