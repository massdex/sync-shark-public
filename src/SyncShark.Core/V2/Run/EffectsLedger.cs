using System.Globalization;
using System.Text;

namespace Massdex.SyncShark.V2;

/// <summary>One streaming CSV owner for simulated endpoint effects and durable live result commits.</summary>
internal sealed class EffectsLedger : IDisposable
{
    internal const string IncompleteReason = SqliteStateStore.EffectsLedgerIncompleteReason;
    private readonly TextWriter? _writer;
    private readonly object _gate = new();
    private readonly bool _simulated;
    private readonly string _path;
    private Exception? _failure;
    private bool _disposed;
    internal int Run { get; set; } = 1;
    internal Exception? Failure { get { lock (_gate) return _failure; } }

    internal EffectsLedger(string path, bool simulated)
    {
        _simulated = simulated;
        _path = path;
        try
        {
            _writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false));
            WriteHeader();
        }
        catch (Exception error) when (DiagnosticFailure(error)) { _failure = error; }
    }

    internal EffectsLedger(TextWriter writer, bool simulated)
    {
        _writer = writer;
        _simulated = simulated;
        _path = "diagnostic stream";
        WriteHeader();
    }

    internal void Unavailable(string reason)
    {
        lock (_gate) _failure ??= new IOException(reason);
    }

    private static bool DiagnosticFailure(Exception error) => error is IOException or UnauthorizedAccessException;

    private void WriteHeader() => Write("run", "kind", "side", "provider", "path", "size", "hash", "simulated");

    internal void Simulated(RehearsalEffect effect) => Record(effect.Kind, effect.Side, effect.Provider,
        effect.Path, effect.Size, effect.Hash);

    // Storage publishes its already-computed durable outcome, outside its shared gate. The ledger
    // only chooses endpoint-effect action kinds; it never repeats persistence outcome coercions.
    internal void Committed(PairDef pair, IReadOnlyList<CommittedAction> results)
    {
        if (_simulated) return;
        foreach (var row in results)
        {
            var (action, result, outcome) = row;
            if (outcome != Outcome.Done || action.Kind is ActionKind.Adopt or ActionKind.Forget or ActionKind.Hold) continue;
            var item = (action.Target == Side.A ? result.NewA : result.NewB) ?? action.Source ?? action.ExpectedTarget;
            Record(action.Kind.ToString(), action.Target.ToString(),
                (action.Target == Side.A ? pair.A : pair.B).Provider, action.Path, item?.Size ?? 0, item?.Hash);
        }
    }

    internal Notice IncompleteNotice(string pairId) => new(pairId, 0, NoticeKind.Info, string.Empty,
        $"Effects ledger '{_path}' is incomplete: {Failure?.Message}. Committed effects remain committed. " +
        "Check destination permissions and free space, then rerun with a new --export path. " +
        "Retry sync does not repair the prior export; Ignore dismisses this diagnostic.", IncompleteReason);

    // Never called from Dispose: a crash, cancellation, or exceptional exit must not claim completeness.
    internal void Complete() => Write(Run.ToString(CultureInfo.InvariantCulture), "complete", null, null, null, null, null,
        _simulated ? "true" : "false");

    private void Record(string kind, string side, string provider, string path, long size, string? hash) =>
        Write(Run.ToString(CultureInfo.InvariantCulture), kind, side, provider, path,
            size.ToString(CultureInfo.InvariantCulture), hash, _simulated ? "true" : "false");

    private void Write(params string?[] fields)
    {
        lock (_gate)
        {
            if (_failure is not null || _writer is null || _disposed) return;
            try
            {
                for (var i = 0; i < fields.Length; i++)
                {
                    if (i != 0) _writer.Write(',');
                    _writer.Write(CsvFields.Encode(fields[i]));
                }
                _writer.WriteLine();
                _writer.Flush();
            }
            catch (Exception error) when (DiagnosticFailure(error)) { _failure = error; }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _writer?.Dispose(); }
            catch (Exception error) when (DiagnosticFailure(error)) { _failure ??= error; }
        }
    }
}
