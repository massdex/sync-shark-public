namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

/// <summary>Records echo retirement and ordinary selection in one ordered log, observing certified rollback before it escapes.</summary>
internal sealed class EchoRollbackStore(IStateStore inner) : ForwardingStateStore(inner)
{
    public List<(string Call, long Work)> Order { get; } = [];
    public Action<ItemStoreMutationException>? RolledBack { get; init; }

    public void Record(string call, long work = 0)
    {
        lock (Order) Order.Add((call, work));
    }

    public override (int Settled, bool More) SettleEchoes(string pairId, long expectedGeneration, PathScope scope,
        Func<ChangeNotice, SyncedEntry?, bool, bool> settles, int pageSize)
    {
        Record(nameof(SettleEchoes));
        try { return base.SettleEchoes(pairId, expectedGeneration, scope, settles, pageSize); }
        catch (ItemStoreMutationException error)
        {
            RolledBack?.Invoke(error);
            throw;
        }
    }

    public override WorkSelection? BeginSelection(WorkSelection selection)
    {
        Record(nameof(BeginSelection), selection.AnchorWorkId);
        return base.BeginSelection(selection);
    }

    public override bool EndWorkPass(string pairId)
    {
        Record(nameof(EndWorkPass));
        return base.EndWorkPass(pairId);
    }
}
