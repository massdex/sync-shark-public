namespace Massdex.SyncShark.Engine.Run;

/// <summary>Starts or records Repair for lost continuity; commits a fresh source base and its affected Repair debts together.</summary>
public static class Monitor
{
    /// <summary>Observe an already acquired page without reading the source again.</summary>
    internal static Task ObserveAsync(PairConfig pair, Side side, ChangePage page, IStore store, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Only a ContinuityLost page carrying its verified fresh Position or Cursor resets the source; read failures retain the old boundary. Normal contiguous advancement does not request Repair; use the atomic source-reset operation only for lost continuity. For a volume reset derive every member, including switched-off pairs, from PairsAsync and RootIdentity.LocalVolumeKey().</summary>
    public static Task ObserveAsync(PairConfig pair, ISide side, IStore store, CancellationToken ct) => throw new NotImplementedException();
}
