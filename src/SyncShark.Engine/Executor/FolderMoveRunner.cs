using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Hands oversized moves into durable custody and fills each recorded destination without adopting occupants.</summary>
internal sealed class FolderMoveRunner(ExecutorContext context)
{
    // All-too-many results are bisected into a first and a second half, never a configurable fan-out.
    private const int Halves = 2;
    private Dictionary<string, FolderMove> _byId = null!;
    private Dictionary<string, List<string>> _children = null!;
    private FolderMove _root = null!;
    private Action? _tick;
    private long _moved;
    internal bool Advanced { get; private set; }
    private string PairId => context.Pair.PairId;
    private IFolderMoveSide Provider => (IFolderMoveSide)context.Dropbox;

    internal async Task<Entry> MoveAsync(Side written, Entry expected, string target, CancellationToken ct)
    {
        if (written == Side.Local || !expected.IsFolder)
            return await context.For(written).MoveAsync(expected, target, ct);
        var pending = context.Store.FolderMoves(PairId, expected.Id)
            .FirstOrDefault(row => row.ParentId is null && row.Phase < FolderMovePhase.Relabelled);
        if (pending is not null) throw Hold(pending, TextKey.FailMoveInParts);
        try { return await context.Dropbox.MoveAsync(expected, target, ct); }
        catch (ItemFailedException error) when (error.Cause == TextKey.FailDropboxTooManyFiles)
        {
            if (PathNames.CaseOnly(expected.Path, target)) throw;
        }
        var source = await context.Dropbox.ReadOneAsync(expected.Path, ct);
        if (source is not { IsFolder: true } || source.Id != expected.Id)
            throw new ChangedUnderneathException(expected.Path);
        await ProveNoMountAsync(source, ct);
        if (await context.Dropbox.ReadOneAsync(target, ct) is not null) throw new PathOccupiedException(target);
        var root = new FolderMove(expected.Id, expected.Id, null, expected.Path, target, null,
            FolderMovePhase.Armed, null, null, 0);
        context.Store.SetFolderMove(PairId, context.ExpectedGeneration, null, root);
        throw Hold(root, TextKey.FailMoveInParts);
    }

    private static bool CannotSplit(Entry entry) =>
        FolderDeletes.KeepsMount(entry, FolderRemovalAuthority.Inferred) ||
        entry.IsFolder && entry.Skip == nameof(TextKey.SkipReservedName);

    private async Task ProveNoMountAsync(Entry source, CancellationToken ct)
    {
        if (CannotSplit(source)) throw new ItemFailedException(source.Path, TextKey.FailMoveSharedFolder, null);
        try
        {
            await foreach (var listed in context.Dropbox.ReadAllAsync(source.Path,
                new ListOptions(Recursive: true, IncludeOutOfScope: true), ct))
                if (listed.Entry is { } entry && CannotSplit(entry))
                    throw new ItemFailedException(source.Path, TextKey.FailMoveSharedFolder, null);
        }
        catch (InventoryIncompleteException error)
        {
            throw ItemFailures.InventoryFailure(error, source.Path, Side.Dropbox);
        }
    }

    internal async Task<Entry> FinishAsync(IReadOnlyList<FolderMove> rows, CancellationToken ct, Action? tick = null)
    {
        _tick = tick;
        _root = rows.Single(row => row.ParentId is null);
        _byId = new(StringComparer.Ordinal);
        _children = new(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            _byId.Add(row.SourceId, row);
            if (row.ParentId is { } parent)
            {
                if (!_children.TryGetValue(parent, out var children)) _children[parent] = children = [];
                children.Add(row.SourceId);
            }
        }
        try
        {
            return await FillAsync(_root.SourceId, ct);
        }
        catch (PathOccupiedException) when (_byId[_root.SourceId].Phase == FolderMovePhase.Armed) { throw; }
        catch (ItemFailedException error) when (error.Move is not null) { throw; }
        catch (Exception error) when (ItemFailures.IsItemError(error))
        {
            throw Hold(_root, TextKey.FailMoveInParts, error);
        }
    }

    // Persistence owns the fence and CAS; the fill indexes advance only after that write succeeds.
    private void Write(FolderMove? expected, FolderMove? next)
    {
        context.Store.SetFolderMove(PairId, context.ExpectedGeneration, expected, next);
        Advanced |= next is null || expected is not null && next.Phase != expected.Phase;
        if (next is null) _byId.Remove(expected!.SourceId);
        else
        {
            if (expected is null && next.ParentId is { } parent)
            {
                if (!_children.TryGetValue(parent, out var children)) _children[parent] = children = [];
                children.Add(next.SourceId);
            }
            _byId[next.SourceId] = next;
        }
    }

    private void Update(FolderMove row, FolderMove next) => Write(row, next with { Generation = checked(row.Generation + 1) });

    private async Task<Entry> RequireIdentityAsync(string path, string id, CancellationToken ct)
    {
        var entry = await context.Dropbox.ReadOneAsync(path, ct);
        if (entry is not { IsFolder: true } || entry.Id != id) throw Hold(_root, TextKey.FailChanged);
        return entry;
    }

    private async Task<Entry> FillAsync(string id, CancellationToken ct)
    {
        var row = _byId[id];
        if (row.Phase == FolderMovePhase.Armed)
        {
            var created = await Provider.CreateFolderExclusiveAsync(row.DestinationPath, ct);
            _tick?.Invoke();
            Update(row, row with { DestinationId = created.Id, Phase = FolderMovePhase.Filling });
        }
        while (_byId[id].Phase < FolderMovePhase.Filled)
        {
            ct.ThrowIfCancellationRequested();
            var refusal = await FillChildrenAsync(id, ct);
            row = _byId[id];
            var recovered = row.Launch is null ? (Progress: 0, Armed: false, Refusal: (TextKey?)null)
                : await AccountAsync(row, row.JobId is null ? null : await JoinAsync(row, ct), ct);
            refusal ??= recovered.Refusal;
            row = _byId[id];
            await RequireIdentityAsync(row.SourcePath, row.SourceId, ct);
            var pass = await PassAsync(row, ct);
            refusal ??= pass.Refusal;
            if (refusal is { } cause) throw Hold(_root, cause);
            if (!pass.Launched && ChildrenFilled(id))
            {
                row = _byId[id];
                Update(row, row with { Phase = FolderMovePhase.Filled });
            }
            else if (pass.Progress + recovered.Progress == 0 && !pass.Armed && !recovered.Armed)
                throw Hold(_root, TextKey.FailMoveInParts);
        }
        row = _byId[id];
        return await RequireIdentityAsync(row.DestinationPath, row.DestinationId!, ct);
    }

    private async Task<TextKey?> FillChildrenAsync(string parent, CancellationToken ct)
    {
        TextKey? refusal = null;
        if (!_children.TryGetValue(parent, out var children)) return null;
        foreach (var id in children)
        {
            if (!_byId.TryGetValue(id, out var child) || child.Phase >= FolderMovePhase.Filled) continue;
            try { await FillAsync(id, ct); }
            catch (PathOccupiedException) when (_byId[id].Phase == FolderMovePhase.Armed)
            {
                Write(_byId[id], null);
                refusal ??= TextKey.FailChanged;
            }
        }
        return refusal;
    }

    private bool ChildrenFilled(string parent) => !_children.TryGetValue(parent, out var children) ||
        children.All(id => !_byId.TryGetValue(id, out var row) || row.Phase >= FolderMovePhase.Filled);

    private async Task<(int Progress, bool Armed, TextKey? Refusal, bool Launched)> PassAsync(FolderMove row, CancellationToken ct)
    {
        var progress = 0;
        var armed = false;
        TextKey? refusal = null;
        var launched = false;
        var members = new List<(string Id, string Name)>(DropboxApiLimits.BatchMaxEntries);
        try
        {
            await foreach (var listed in context.Dropbox.ReadAllAsync(row.SourcePath,
                new ListOptions(Recursive: false, IncludeOutOfScope: true, IncludeDeleted: false), ct))
            {
                if (listed.Entry is not { } entry || _byId.TryGetValue(entry.Id, out var child) && child.ParentId == row.SourceId)
                    continue;
                if (CannotSplit(entry)) { refusal ??= TextKey.FailMoveSharedFolder; continue; }
                members.Add((entry.Id, PathNames.Name(entry.Path)));
                if (members.Count < DropboxApiLimits.BatchMaxEntries) continue;
                var result = await LaunchAsync(row.SourceId, members.ToArray(), ct);
                progress += result.Progress;
                armed |= result.Armed;
                refusal ??= result.Refusal;
                launched = true;
                members.Clear();
            }
        }
        catch (InventoryIncompleteException error)
        {
            throw ItemFailures.InventoryFailure(error, row.SourcePath, Side.Dropbox);
        }
        _tick?.Invoke();
        if (members.Count != 0)
        {
            var result = await LaunchAsync(row.SourceId, members.ToArray(), ct);
            progress += result.Progress;
            armed |= result.Armed;
            refusal ??= result.Refusal;
            launched = true;
        }
        return (progress, armed, refusal, launched);
    }

    private async Task<(int Progress, bool Armed, TextKey? Refusal)> LaunchAsync(string id,
        IReadOnlyList<(string Id, string Name)> members, CancellationToken ct)
    {
        var row = _byId[id];
        await RequireIdentityAsync(row.SourcePath, row.SourceId, ct);
        await RequireIdentityAsync(row.DestinationPath, row.DestinationId!, ct);
        Update(row, row with { Launch = members });
        row = _byId[id];
        var attempt = new RequestAttempt(context.Opportunity, AttemptOwner.Recovery);
        FolderSubmitResult submitted;
        var success = false;
        try
        {
            submitted = await Provider.SubmitMoveBatchAsync(members.Select(member =>
                (member.Id, PathNames.Child(row.DestinationPath, member.Name))).ToArray(), attempt, ct);
            success = submitted is FolderSubmitResult.Accepted or FolderSubmitResult.Terminal;
        }
        finally { context.Admission.ActionCompleted(success); }
        _tick?.Invoke();
        IReadOnlyList<FolderMemberResult>? results = null;
        switch (submitted)
        {
            case FolderSubmitResult.Accepted accepted:
                Update(row, row with { JobId = accepted.JobId });
                row = _byId[id];
                results = await JoinAsync(row, ct);
                break;
            case FolderSubmitResult.Terminal terminal:
                results = terminal.Results;
                await RequireIdentityAsync(row.DestinationPath, row.DestinationId!, ct);
                break;
            case FolderSubmitResult.KnownNoEffect:
                Update(row, row with { Launch = null, JobId = null });
                return (0, false, null);
        }
        return await AccountAsync(row, results, ct);
    }

    private async Task<IReadOnlyList<FolderMemberResult>> JoinAsync(FolderMove row, CancellationToken ct)
    {
        while (true)
        {
            var attempt = new RequestAttempt(context.Opportunity, AttemptOwner.Recovery);
            var result = await Provider.ReadMoveBatchAsync(row.JobId!, row.Launch!.Count, attempt, ct);
            _tick?.Invoke();
            if (result is FolderJobResult.Terminal terminal)
            {
                await RequireIdentityAsync(row.DestinationPath, row.DestinationId!, ct);
                return terminal.Results;
            }
            await Task.Delay(context.Settings.FolderDeletePollInterval, context.Time, ct);
        }
    }

    private async Task<(int Progress, bool Armed, TextKey? Refusal)> AccountAsync(FolderMove row,
        IReadOnlyList<FolderMemberResult>? results, CancellationToken ct)
    {
        var launch = row.Launch!;
        if (launch.Count > 1 && results is not null && results.All(result =>
            result is FolderMemberResult.KnownNoEffect { Cause: TextKey.FailDropboxTooManyFiles }))
        {
            Update(row, row with { Launch = null, JobId = null });
            var middle = (launch.Count + 1) / Halves;
            var first = await LaunchAsync(row.SourceId, launch.Take(middle).ToArray(), ct);
            var second = await LaunchAsync(row.SourceId, launch.Skip(middle).ToArray(), ct);
            return (first.Progress + second.Progress, first.Armed || second.Armed, first.Refusal ?? second.Refusal);
        }
        var progress = 0;
        var armed = false;
        TextKey? refusal = null;
        if (results is not null)
        {
            foreach (var result in results)
            {
                var member = launch[result.Ordinal];
                switch (result)
                {
                    case FolderMemberResult.Receipt:
                    case FolderMemberResult.KnownNoEffect { Cause: TextKey.FailChanged }:
                        Moved(); progress++; break;
                    case FolderMemberResult.KnownNoEffect { Cause: TextKey.ClashNameTaken }:
                        if ((await context.Dropbox.ReadOneAsync(PathNames.Child(row.DestinationPath, member.Name), ct))?.Id == member.Id)
                        { Moved(); progress++; }
                        else refusal ??= TextKey.FailChanged;
                        break;
                    case FolderMemberResult.KnownNoEffect { Cause: TextKey.FailDropboxTooManyFiles }:
                        var nested = await ArmNestedAsync(row, member, ct);
                        armed |= nested is null;
                        refusal ??= nested;
                        break;
                    case FolderMemberResult.KnownNoEffect refused:
                        refusal ??= refused.Cause ?? TextKey.DropboxRefused;
                        break;
                }
            }
        }
        progress += await ResolveLaunchAsync(row, results, ct);
        Update(_byId[row.SourceId], _byId[row.SourceId] with { Launch = null, JobId = null });
        return (progress, armed, refusal);
    }

    private async Task<TextKey?> ArmNestedAsync(FolderMove parent, (string Id, string Name) member, CancellationToken ct)
    {
        if (_byId.ContainsKey(member.Id)) return null;
        var source = PathNames.Child(parent.SourcePath, member.Name);
        var entry = await context.Dropbox.ReadOneAsync(source, ct);
        if (entry is not { IsFolder: true } || entry.Id != member.Id) return TextKey.FailChanged;
        if (CannotSplit(entry)) return TextKey.FailMoveSharedFolder;
        Write(null, new FolderMove(member.Id, parent.RootId, parent.SourceId, source,
            PathNames.Child(parent.DestinationPath, member.Name), null, FolderMovePhase.Armed, null, null, 0));
        return null;
    }

    private async Task<int> ResolveLaunchAsync(FolderMove row, IReadOnlyList<FolderMemberResult>? results, CancellationToken ct)
    {
        var progress = 0;
        for (var ordinal = 0; ordinal < row.Launch!.Count; ordinal++)
        {
            if (results is not null && results[ordinal] is not FolderMemberResult.Unknown) continue;
            var member = row.Launch[ordinal];
            var source = PathNames.Child(row.SourcePath, member.Name);
            var move = new IssueSubject.UnknownMove(member.Id, source, PathNames.Child(row.DestinationPath, member.Name), null, true);
            var observed = await ((IIdentitySide)context.Dropbox).ReadByIdAsync(member.Id, ct);
            switch (UnknownMoveCheck.Decide(move, observed))
            {
                case UnknownMoveCheck.Outcome.Adopt: Moved(); progress++; break;
                case UnknownMoveCheck.Outcome.ReplanFromSource: break;
                default:
                    var items = new List<IssueItem> { new(Side.Dropbox, source, member.Id) };
                    if (context.Store.SyncedById(PairId, Side.Dropbox, member.Id) is { } bound)
                        items.Add(new IssueItem(Side.Local, bound.LocalPath, bound.LocalId));
                    context.Store.Commit(PairId, StateChange.Empty with
                    {
                        Raise = [(IssueKind.Failure, TextKey.DropboxRefused, source, new IssueSubject(items, Side.Dropbox) { Move = move })]
                    });
                    progress++;
                    break;
            }
        }
        return progress;
    }

    private void Moved()
    {
        _moved++;
        context.Events(RunEvent.Stage(TextKey.StageFolderMoveParts, count: _moved));
    }

    private static ItemFailedException Hold(FolderMove root, TextKey cause, Exception? inner = null) =>
        new(root.SourcePath, cause, inner)
        {
            Disposition = FailureDisposition.UnknownEffect,
            Move = new(root.SourceId, root.SourcePath, root.DestinationPath, null, true)
        };
}
