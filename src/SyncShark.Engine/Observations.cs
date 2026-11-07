using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.Engine;

/// <summary>Facts the Executor gathers for Renames.For (section 6.3).
/// LocalHash: the content hash of LocalAtSynced when only its metadata changed (R4-M09), else null.
/// LocalDestination / DropboxDestination: where Follow moves that side when the other side wins
/// (Parents.Destination of the other side's moved path, null when it has no candidate).</summary>
public sealed record Observations(Entry? LocalAtSynced, Entry? DropboxAtSynced, bool OldOrNewSkipped,
    bool OldOrNewHeld, bool NewOutOfScope, bool CarriesOtherPair, string? LocalHash,
    string? LocalDestination, string? DropboxDestination)
{
    /// <summary>The entry observed at the saved path on a side; any non-local side selects Dropbox.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Entry? AtSynced(Side side) => side == Side.Local ? LocalAtSynced : DropboxAtSynced;
}
