using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.V2;

/// <summary>Records compact manual-run display rows without retaining executor action or result graphs.</summary>
public sealed class ObservedExecutor : IExecutor
{
    /// <summary>Display-only action identity and outcome; null outcome denotes a held action.</summary>
    public sealed record Observation(string Path, ActionKind Kind, Side Target, Outcome? Outcome, string? Reason);

    // Display detail is not durable history; keep provider payloads out of this window-session collection.
    private const int MaximumReasonLength = 512;
    private readonly IExecutor _inner;
    private readonly AsyncLocal<Session?> _current = new();

    /// <summary>Wraps the real executor without changing the results it returns.</summary>
    public ObservedExecutor(IExecutor inner) => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <summary>Records only this async flow until disposed; background runs keep no observation state.</summary>
    public Session Begin()
    {
        var session = new Session(this, _current.Value);
        _current.Value = session;
        return session;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ActionResult> ApplyAsync(Plan p, IEndpointWriter a, IEndpointWriter b,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var session = _current.Value is { IsOpen: true } active ? active : null;
        var planned = session is null ? null : Index(p.Actions, session, recordHeld: true);
        await foreach (var result in _inner.ApplyAsync(p, a, b, ct).ConfigureAwait(false))
        {
            if (planned is not null && planned.TryGetValue(result.ActionId, out var row))
                session!.Record(WithResult(row, result));
            yield return result;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ActionResult>> ReconcileAsync(IReadOnlyList<SyncAction> intents, long gen,
        IEndpointWriter a, IEndpointWriter b, CancellationToken ct, IProgress<int>? progress = null)
    {
        var session = _current.Value is { IsOpen: true } active ? active : null;
        var planned = session is null ? null : Index(intents, session, recordHeld: false);
        var results = await _inner.ReconcileAsync(intents, gen, a, b, ct, progress).ConfigureAwait(false);
        if (planned is not null)
            foreach (var result in results)
                if (planned.TryGetValue(result.ActionId, out var row)) session!.Record(WithResult(row, result));
        return results;
    }

    private static Dictionary<string, Observation> Index(IReadOnlyList<SyncAction> actions, Session session,
        bool recordHeld)
    {
        var planned = new Dictionary<string, Observation>(actions.Count, StringComparer.Ordinal);
        foreach (var action in actions)
        {
            var row = new Observation(action.Path, action.Kind, action.Target, null, null);
            planned[action.ActionId] = row;
            if (recordHeld && action.Kind == ActionKind.Hold) session.Record(row);
        }
        return planned;
    }

    private static Observation WithResult(Observation row, ActionResult result) => row with
    {
        Outcome = result.ProviderNameRefused ? Outcome.Skipped : result.Outcome,
        Reason = result.Reason is { Length: > MaximumReasonLength } reason
            ? reason[..MaximumReasonLength] + "…" : result.Reason
    };

    /// <summary>Owns one manual run's latest display row per path; draining or disposing releases all rows.</summary>
    public sealed class Session : IDisposable
    {
        private readonly ObservedExecutor _owner;
        private readonly Session? _previous;
        private readonly object _gate = new();
        // Case-collision holds describe distinct displayed spellings, even on case-insensitive endpoints.
        private Dictionary<string, Observation>? _rows = new(StringComparer.Ordinal);

        internal Session(ObservedExecutor owner, Session? previous)
        {
            _owner = owner;
            _previous = previous;
        }

        internal bool IsOpen
        {
            get { lock (_gate) return _rows is not null; }
        }

        internal void Record(Observation row)
        {
            // Successful bookkeeping has no display row and must not hide an earlier transfer at this path.
            if (row.Outcome == Outcome.Done && row.Kind is
                ActionKind.MkDir or ActionKind.Adopt or ActionKind.Forget or ActionKind.Hold) return;
            lock (_gate)
                if (_rows is not null) _rows[row.Path] = row;
        }

        /// <summary>Transfers the finished rows to the caller and closes recording, including late completions.</summary>
        public IReadOnlyList<Observation> Take()
        {
            lock (_gate)
            {
                var rows = _rows?.Values.ToArray() ?? [];
                _rows = null;
                return rows;
            }
        }

        /// <summary>Releases this session only, even when another async flow is still recording.</summary>
        public void Dispose()
        {
            lock (_gate)
            {
                _rows = null;
            }
            if (ReferenceEquals(_owner._current.Value, this)) _owner._current.Value = _previous;
        }
    }
}
