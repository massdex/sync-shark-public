using System.Globalization;

namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

/// <summary>The bound root and acquired journal boundary a Local evidence read requires, as a real host leaves them
/// after its source bases are established.</summary>
internal static class SourceFixture
{
    public static PairConfig Bound(PairConfig pair) => pair with
    {
        Roots = new RootBinding(pair.LocalRoot, pair.DropboxRoot, pair.ConnectionId, new DirectoryIdentity(1, 1, null),
            new DropboxRootIdentity("account-id", "namespace-id", "namespace-id", "folder-id"))
    };

    public static void AcquireJournal(IStateStore store, PairConfig pair)
    {
        var domain = pair.Roots!.Local.VolumeSerialNumber.ToString(CultureInfo.InvariantCulture);
        if (store.ReadJournal(domain) is not null) return;
        var source = store.ReadSource(pair.PairId, Side.Local)!.Value;
        var member = (pair.PairId, store.ReadPairExecution(pair.PairId)!.Value.Generation, source.Epoch,
            source.Revision, false);
        Check.Require(store.AcquireJournalPage(domain, null, new JournalPosition(1, 1), [member], []) is not null,
            "the fixture journal position is acquired");
    }
}
