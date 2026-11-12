using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.Engine;

/// <summary>The keep-both labels chosen before the lock (section 7.8). Reuse: a finished copy with the right content.</summary>
/// <remarks>An omitted leg has a null label and Reuse=false; both labels null is invalid.</remarks>
public sealed record KeepBothLabels(string? DropboxLabel, bool ReuseDropbox, string? LocalLabel, bool ReuseLocal)
{
    /// <summary>The label and reuse fact for a side; any non-local side selects the Dropbox pair.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (string? Label, bool Reused) On(Side side) =>
        side == Side.Local ? (LocalLabel, ReuseLocal) : (DropboxLabel, ReuseDropbox);
}
