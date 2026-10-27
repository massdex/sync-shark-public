namespace Massdex.SyncShark.V2;

/// <summary>One atomic decision-byte reservation pool, owned by one admitted batch.</summary>
internal sealed class ReconciliationBudget(IReadOnlyList<string> paths) : IReconciliationBudget
{
    private long _decisionBytes, _effectReadBytes, _effectWrittenBytes;
    public IReadOnlyList<string> Paths => paths;
    internal HashSet<string> Aliases { get; } = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> AliasPaths => Aliases;
    internal long DecisionByteLimit { get; set; }
    internal long DecisionBytes => Interlocked.Read(ref _decisionBytes);
    internal long EffectReadBytes => Interlocked.Read(ref _effectReadBytes);
    internal long EffectWrittenBytes => Interlocked.Read(ref _effectWrittenBytes);
    public void RecordEffectRead(int bytes) => Interlocked.Add(ref _effectReadBytes, bytes);
    public void RecordEffectWrite(int bytes) => Interlocked.Add(ref _effectWrittenBytes, bytes);
    public bool OwnsDecisionPath(string path) => Paths.Contains(path, StringComparer.OrdinalIgnoreCase) || Aliases.Contains(path);

    public bool AdmitDecisionHash(ItemState item)
    {
        var bytes = Math.Max(default(long), item.Size);
        while (true)
        {
            var used = Interlocked.Read(ref _decisionBytes);
            if (bytes > DecisionByteLimit - used)
            {
                RunTrace.Emit($"reconciliation hash deferred: path={item.Path} bytes={bytes} remaining={DecisionByteLimit - used}");
                return false;
            }
            if (Interlocked.CompareExchange(ref _decisionBytes, used + bytes, used) == used) return true;
        }
    }
}
