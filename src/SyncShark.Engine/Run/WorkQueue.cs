namespace Massdex.SyncShark.Engine.Run;

/// <summary>Orders overlapping work while unrelated changes and Repair discoveries run concurrently.</summary>
public sealed class WorkQueue
{
    /// <summary>The exact input cannot yet be reproduced from its durable notice.</summary>
    public sealed class Unsettled : Exception { }

    private sealed class Input(string[] keys, Change? change)
    {
        internal readonly string[] Keys = keys;
        internal readonly Change? Change = change;
        internal readonly TaskCompletionSource Settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal HashSet<Input> Before = [];
        internal TaskCompletionSource? Changed;
        internal string[] Origins = [], Destinations = [];
        internal string? Removal;
        internal bool Started, Failed, Consumed;
        internal long Order;
    }

    /// <summary>The runner owns this pair's single pending-job check.</summary>
    public Executor.Unfinished Unfinished { get; internal set; } = null!;

    private readonly object gate = new();
    private readonly SortedSet<string> occupied = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LinkedList<Input>> byKey = new(StringComparer.Ordinal);
    private readonly HashSet<Input> postponed = [];
    private readonly Task[] prefix = [Task.CompletedTask, Task.CompletedTask];
    private long order;
    private JournalPosition? settledLocal;
    private string? settledDropbox;

    /// <summary>Both source prefixes must cover the fresh physical-hop capture; a move receipt or empty queue proves nothing.</summary>
    public bool CoversHop((JournalPosition? Local,string? Dropbox) throughHop)
    {
        lock (gate) return throughHop.Local is { } at && settledLocal is { } pc && pc.JournalId == at.JournalId &&
            pc.NextUsn >= at.NextUsn && throughHop.Dropbox != null && throughHop.Dropbox == settledDropbox;
    }

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
        var entries = page.Changes.Select(c => new Input(Keys([c.Path, c.OldPath, c.Observed?.Path]), c)
        {
            Origins = c.Kind == ChangeKind.Renamed && c.OldPath != null ? Keys([c.OldPath]) : [],
            Destinations = c.Kind == ChangeKind.Renamed && c.Path != null ? Keys([c.Path]) : [],
            Removal = c.Kind == ChangeKind.Deleted && c.Path != null ? PathNames.Key(c.Path) : null
        }).ToArray();
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
                lock (gate)
                {
                    if (page.Position != null) settledLocal = page.ContinuityLost ? null : page.Position;
                    if (page.Cursor != null) settledDropbox = page.ContinuityLost ? null : page.Cursor;
                    completed.TrySetResult();
                }
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
        var item = new Input(Keys(names), null)
        {
            Origins = bindingCandidates.Select(c => PathNames.Key(c.Synced.PathOn(c.Side))).ToArray(),
            Destinations = bindingCandidates.Select(c => PathNames.Key(c.Moved.Path)).ToArray()
        };
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
        var conflicts = item.Keys.SelectMany(Matches).Distinct(StringComparer.Ordinal).ToArray();
        item.Before = conflicts.Select(key => byKey[key].Last!.Value).ToHashSet();
        foreach (var prior in postponed)
            if (prior.Keys.Intersect(conflicts,StringComparer.Ordinal).Any()) item.Before.Add(prior);
        if (item.Origins.Length != 0)
        {
            var waiting = conflicts.SelectMany(k => byKey[k]).Distinct().Where(prior => !prior.Started && !prior.Failed &&
                item.Origins.Any(origin => prior.Destinations.Contains(origin,StringComparer.Ordinal) ||
                    prior.Removal != null && PathNames.IsUnderKey(origin,prior.Removal))).ToArray();
            if (waiting.Length != 0) Postpone(waiting,item);
        }
        foreach (var key in item.Keys)
        {
            if (occupied.Add(key)) byKey.Add(key, new());
            byKey[key].AddLast(item);
        }
    }

    // The known departure and its earlier prerequisites move together ahead of the unsent arrival/removal.
    private void Postpone(Input[] waiting,Input departure)
    {
        var todo = new Stack<Input>(); todo.Push(departure);
        var seen = new HashSet<Input>();
        while (todo.TryPop(out var current))
        {
            if (current.Started || !seen.Add(current)) continue;
            var bypass = new Stack<Input>(waiting.Where(current.Before.Contains));
            if (bypass.Count != 0) current.Changed?.TrySetResult();
            while (bypass.TryPop(out var dependent))
            {
                if (!current.Before.Remove(dependent)) continue;
                foreach (var earlier in dependent.Before)
                    if (current.Before.Add(earlier) && waiting.Contains(earlier)) bypass.Push(earlier);
            }
            foreach (var predecessor in current.Before) todo.Push(predecessor);
        }
        foreach (var dependent in waiting) { dependent.Before.Add(departure); dependent.Changed?.TrySetResult(); postponed.Add(dependent); }
    }

    private async Task Execute(Input item, Func<Task> apply, CancellationToken ct)
    {
        try
        {
            bool run;
            while (true)
            {
                Task wait;
                lock (gate)
                {
                    ct.ThrowIfCancellationRequested();
                    item.Before.RemoveWhere(p => p.Settled.Task.IsCompletedSuccessfully);
                    if (item.Before.Count == 0) { item.Started = true; run = !item.Consumed; break; }
                    var before = Task.WhenAll(item.Before.Select(p => p.Settled.Task));
                    item.Changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    wait = Task.WhenAny(before,item.Changed.Task).Unwrap();
                }
                await wait.WaitAsync(ct).ConfigureAwait(false);
            }
            if (run) await apply().ConfigureAwait(false);
            lock (gate)
            {
                Release(item);
            }
        }
        catch (Unsettled) when (!ct.IsCancellationRequested && item.Change != null)
        {
            // Retry still needs this report. A following page cannot publish its cursor until it is consumed.
            lock (gate) { item.Failed = true; item.Started = false; if (item.Consumed) Release(item); }
            await item.Settled.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception error) { item.Settled.TrySetException(error); throw; }
    }

    private void Release(Input item)
    {
        postponed.Remove(item);
        foreach (var key in item.Keys)
        {
            var list = byKey[key]; list.Remove(item);
            if (list.Count == 0) { byKey.Remove(key); occupied.Remove(key); }
        }
        item.Settled.TrySetResult();
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
                ConsumeHeld = () => { lock (gate) { if (!i.Started && !i.Consumed) { i.Consumed = true; if (i.Failed) Release(i); } } }
            }).ToArray();
        }
    }
}
