using System.ComponentModel;
using System.Runtime.CompilerServices;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Local;

/// <summary>The local folder walk and single-item read (section 4.2).</summary>
public sealed class LocalListing(PairConfig pair, PathScope scope,
    Action<string, Entry?, TextKey, bool> reportInventoryFailure)
{
    private readonly List<string> _staleStages = new();

    // One directory enumeration yields the entries, stale stages, identities and per-folder failures for one walk lifetime.
    /// <summary>Lists local entries and collects stale stages during a full recursive walk.</summary>
    public async IAsyncEnumerable<Listed> Walk(string under, ListOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var fullWalk = under.Length == 0 && options.Recursive;
        if (fullWalk) _staleStages.Clear();
        var folders = new Stack<Entry?>();
        folders.Push(null);
        // One carrier for the sequential walk; a walk nobody asks about allocates none and reads no identity.
        var opened = options.DirectoryAcquired is null && !options.RequireListedIdentity ? null : new StrongBox<long>();
        long walked = 0;
        await Task.CompletedTask;
        while (folders.TryPop(out var observed))
        {
            ct.ThrowIfCancellationRequested();
            var folder = observed?.Path ?? under;
            var directory = FullDirectory(folder);
            // The root's own handle must match the saved identity; a checked path cannot authenticate it.
            var expected = folder.Length == 0 ? pair.Roots?.Local : null;
            var acquiring = opened is not null && folder.Length != 0;
            using var items = (acquiring ? WindowsDirectoryListing.List(directory, opened!)
                : expected is null ? WindowsDirectoryListing.List(directory)
                : WindowsDirectoryListing.ListRoot(directory, expected)).GetEnumerator();
            Exception? failure;
            var verified = false;
            while (MoveNext(items, ct, out failure))
            {
                if (!verified) RequireListed(options, acquiring, opened, observed, folder);
                verified = true;
                var item = items.Current;
                ct.ThrowIfCancellationRequested();
                var path = PathNames.Child(folder, item.Name);
                var isFolder = (item.Attributes & FileAttributes.Directory) != 0;
                var excluded = scope.Excludes(path, null, isFolder);
                if (StageNames.IsStage(item.Name) && !isFolder)
                {
                    if (fullWalk && !excluded && !PathRules.IsUnsafeAttributes(item.Attributes))
                        _staleStages.Add(LocalPaths.Full(pair.LocalRoot, path));
                    continue;
                }
                if (!options.IncludeOutOfScope && excluded) continue;
                var entry = Classify(path, item, isFolder);
                yield return new Listed(path, entry);
                if (options.Recursive && entry.IsFolder && entry.Skip is null) folders.Push(entry);
            }
            failure ??= Close(items);
            if (failure is null && !verified) RequireListed(options, acquiring, opened, observed, folder);
            options.Progress?.Invoke(folder, ++walked);
            if (failure is null)
            {
                if (!acquiring) continue;
                ct.ThrowIfCancellationRequested();
                options.DirectoryAcquired?.Invoke(folder, LocalIdentity.FileIdText(unchecked((ulong)opened!.Value)));
                continue;
            }
            var cause = LocalErrors.Cause(failure, TextKey.ReadLocalFailed);
            // A scoped walk's own root has no observed entry to report; its typed failure below reaches the caller that
            // observed it (ResolutionRunner), as the reporter accepts only an observed folder or the whole pair root.
            if (IssuePolicy.IsLocalAccessRetryCause(cause) && (observed is not null || under.Length == 0))
                reportInventoryFailure(folder, observed, cause, observed is null);
            if (!fullWalk || observed is null)
            {
                var failed = new ItemFailedException(folder, cause, failure);
                throw new InventoryIncompleteException(failed.Message, failed);
            }
            // Re-yield the observed identity so even children yielded before the failure are protected.
            yield return new Listed(folder, observed with { Skip = TextKey.SkipUnreadable.ToString() });
        }
    }

    // The enumerated handle must be the listed folder, not a later occupant of its path; the walk's own start has no
    // listed identity, so only descended subfolders are checked.
    private static void RequireListed(ListOptions options, bool acquiring, StrongBox<long>? opened, Entry? observed,
        string folder)
    {
        if (options.RequireListedIdentity && acquiring && observed is not null &&
            LocalIdentity.FileIdText(unchecked((ulong)opened!.Value)) != observed.Id)
            throw new ChangedUnderneathException(folder);
    }

    /// <summary>The entry at path, or null when it or its folder is gone.</summary>
    public Entry? One(string path)
    {
        try
        {
            var name = path.Length == 0 ? "" : Path.GetFileName(LocalPaths.Full(pair.LocalRoot, path));
            var parent = PathNames.Parent(path);
            var item = WindowsDirectoryListing.Named(FullDirectory(parent), name);
            if (item is null) return null;
            var isFolder = (item.Attributes & FileAttributes.Directory) != 0;
            return Classify(PathNames.Child(parent, item.Name), item, isFolder);
        }
        catch (Win32Exception error) when (error.NativeErrorCode is ErrorFileNotFound or ErrorPathNotFound)
        {
            return null;
        }
        catch (Exception error) when (LocalErrors.IsFileFailure(error))
        {
            throw new ItemFailedException(path, LocalErrors.Cause(error, TextKey.ReadLocalFailed), error);
        }
    }

    // OS facts: Win32 ERROR_FILE_NOT_FOUND and ERROR_PATH_NOT_FOUND (the folder is gone, section 4.2).
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

    /// <summary>Stage files found by the last full walk.</summary>
    public IReadOnlyList<string> StaleStages => _staleStages;

    private string FullDirectory(string path) =>
        path.Length == 0 ? pair.LocalRoot : LocalPaths.Full(pair.LocalRoot, path);

    private static bool MoveNext(IEnumerator<DirectoryEntryInfo> items, CancellationToken ct,
        out Exception? failure)
    {
        ct.ThrowIfCancellationRequested();
        failure = null;
        try
        {
            return items.MoveNext();
        }
        catch (Exception error) when (LocalErrors.IsFileFailure(error))
        {
            failure = error;
            return false;
        }
    }

    // Completed enumeration is not proof until the handle has closed; a second Dispose by the caller's using is a no-op.
    private static Exception? Close(IEnumerator<DirectoryEntryInfo> items)
    {
        try
        {
            items.Dispose();
            return null;
        }
        catch (Exception error) when (LocalErrors.IsFileFailure(error))
        {
            return error;
        }
    }

    private static Entry Classify(string path, DirectoryEntryInfo item, bool isFolder)
    {
        var skip = PathRules.IsLink(item.Attributes, item.ReparseTag) ? TextKey.SkipLink.ToString()
            : PathRules.IsOnlineOnlyStub(item.Attributes, item.ReparseTag) ? TextKey.SkipOnlineOnly.ToString()
            : (item.Attributes & FileAttributes.ReparsePoint) != 0 &&
                !PathRules.IsLocalCloudFilesObject(item.Attributes, item.ReparseTag)
                ? TextKey.SkipSpecial.ToString() : null;
        return new Entry(path, isFolder, isFolder ? default : item.Size,
            isFolder ? default : new DateTimeOffset(item.LastWriteTimeUtc),
            LocalIdentity.FileIdText(unchecked((ulong)item.FileId)), "", "", skip);
    }
}
