namespace Massdex.SyncShark.Engine.Run;

/// <summary>Orders overlapping work while unrelated changes and Repair discoveries run concurrently.</summary>
public sealed class WorkQueue
{
    private sealed class Input(string[] keys, Change? change)
    {
        internal readonly string[] Keys = keys;
        internal readonly Change? Change = change;
        internal readonly TaskCompletionSource Settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Predecessors = Task.CompletedTask;
        internal bool Started, Consumed;
        internal long Order;
    }

    /// <summary>The runner owns this pair's single pending-job check.</summary>
    public Executor.Unfinished Unfinished { get; internal set; } = null!;

    private readonly object gate = new();
    private readonly SortedSet<string> occupied = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LinkedList<Input>> byKey = new(StringComparer.Ordinal);
    private readonly Task[] prefix = [Task.CompletedTask, Task.CompletedTask];
    private long order;

    /// <summary>Publish the whole page before callbacks can run. Journal progress follows only its contiguous settled prefix.</summary>
    public Task AdmitAsync(PairConfig pair, ChangePage page, Func<Change,CancellationToken,Task> apply,
        Func<JournalPosition,CancellationToken,Task>? settleJournal, CancellationToken ct) =>
        AdmitAsync(pair, page, apply, settleJournal, null, null, ct);

    /// <summary>Wake received news before dispatch; persist each source boundary in receipt order.</summary>
    public Task AdmitAsync(PairConfig pair, ChangePage page, Func<Change,CancellationToken,Task> apply,
        Func<JournalPosition,CancellationToken,Task>? settleJournal, Func<string,CancellationToken,Task>? settleCursor,
        Func<Change,CancellationToken,Task>? received, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var entries = page.Changes.Select(c => new Input(Keys([c.Path, c.OldPath, c.Observed?.Path]), c)).ToArray();
        Task previous;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            foreach (var item in entries) Register(item);
            var side = page.Position != null || settleJournal != null ? Side.Local : Side.Dropbox;
            previous = prefix[(int)side];
            prefix[(int)side] = completed.Task;
        }
        return FinishPage();

        async Task FinishPage()
        {
            var wakes = received == null ? Task.CompletedTask : Task.WhenAll(entries.Select(i => Wake(i.Change!)));
            async Task Wake(Change change) => await received!(change, ct).ConfigureAwait(false);
            var jobs = entries.Select(item => Execute(item, () => apply(item.Change!, ct), ct)).ToArray();
            try
            {
                await Task.WhenAll(previous, wakes).ConfigureAwait(false);
                for (var i = 0; i < jobs.Length; i++)
                {
                    await jobs[i].ConfigureAwait(false);
                    if (settleJournal != null && page.Position is { } end && i+1 < jobs.Length &&
                        entries[i+1].Change is { Usn: { } next, JournalId: var journal } && journal == end.JournalId &&
                        entries[i].Change!.Usn < next)
                        await settleJournal(new(end.JournalId, next), ct).ConfigureAwait(false);
                }
                if (settleJournal != null && page.Position is { } last)
                    await settleJournal(last, ct).ConfigureAwait(false);
                if (settleCursor != null && page.Cursor is { } cursor) await settleCursor(cursor, ct).ConfigureAwait(false);
                completed.TrySetResult();
            }
            catch (Exception error)
            {
                completed.TrySetException(error);
                try { await Task.WhenAll(jobs).ConfigureAwait(false); } catch { /* The first unsettled boundary is reported below. */ }
                throw;
            }
        }
    }

    /// <summary>Order a prepared row or an entire move against both the original and observed endpoints.</summary>
    public Task AdmitAsync(PairConfig pair, Row? prepared, IReadOnlyList<RenameCandidate> bindingCandidates,
        Func<CancellationToken,Task> apply, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (prepared == null && bindingCandidates.Count == 0) throw new ArgumentException(nameof(bindingCandidates));
        IEnumerable<string?> names = bindingCandidates.SelectMany(c => new[] { c.Moved.Path, c.Synced.LocalPath, c.Synced.DropboxPath });
        if (prepared != null) names = names.Concat(Row.Keys(prepared));
        var item = new Input(Keys(names), null);
        lock (gate) Register(item);
        return Execute(item, () => apply(ct), ct);
    }

    private static string[] Keys(IEnumerable<string?> paths) => paths.Where(p => p != null)
        .Select(p => PathNames.TreeKey(PathNames.Key(p!))).Distinct(StringComparer.Ordinal).DefaultIfEmpty("").ToArray();

    private IEnumerable<string> Matches(string key)
    {
        if (key.Length == 0) return occupied;
        var bounds = PathNames.DescendantBounds(key);
        return PathNames.Ancestors(key).Append("").Append(key).Where(byKey.ContainsKey)
            .Concat(occupied.GetViewBetween(bounds.Low, bounds.High).TakeWhile(k => k != bounds.High));
    }

    private void Register(Input item)
    {
        item.Order = ++order;
        item.Predecessors = Task.WhenAll(item.Keys.SelectMany(Matches).Distinct(StringComparer.Ordinal)
            .Select(key => byKey[key].Last!.Value.Settled.Task).Distinct());
        foreach (var key in item.Keys)
        {
            if (occupied.Add(key)) byKey.Add(key, new());
            byKey[key].AddLast(item);
        }
    }

    private async Task Execute(Input item, Func<Task> apply, CancellationToken ct)
    {
        try
        {
            await item.Predecessors.WaitAsync(ct).ConfigureAwait(false);
            bool run;
            lock (gate) { ct.ThrowIfCancellationRequested(); item.Started = true; run = !item.Consumed; }
            if (run) await apply().ConfigureAwait(false);
            lock (gate)
            {
                foreach (var key in item.Keys)
                {
                    var list = byKey[key]; list.Remove(item);
                    if (list.Count == 0) { byKey.Remove(key); occupied.Remove(key); }
                }
                item.Settled.TrySetResult();
            }
        }
        catch (Exception error) { item.Settled.TrySetException(error); throw; }
    }

    /// <summary>Borrow waiting reports on their own side in receipt order; only explicit acknowledgment consumes them.</summary>
    public IEnumerable<Change> Held(Scope footprint)
    {
        lock (gate)
        {
            var matches = Keys([footprint.LocalPath, footprint.DropboxPath]).SelectMany(Matches)
                .Distinct(StringComparer.Ordinal).SelectMany(k => byKey[k]).Distinct().Where(item =>
                {
                    if (item.Started || item.Consumed || item.Change is not { } c) return false;
                    var root = c.Side == Side.Local ? footprint.LocalPath : footprint.DropboxPath;
                    return root.Length == 0 || new[] { c.Path, c.OldPath }.Any(p => p != null && PathNames.IsSelfOrUnder(p, root));
                });
            return matches.OrderBy(i => i.Order).Select(i => i.Change! with
            {
                ConsumeHeld = () => { lock (gate) { if (!i.Started) i.Consumed = true; } }
            }).ToArray();
        }
    }
}
