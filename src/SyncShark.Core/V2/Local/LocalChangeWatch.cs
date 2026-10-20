namespace Massdex.SyncShark.V2;

/// <summary>
/// Watches one local root with one long-lived <see cref="FileSystemWatcher"/>; a product-owned or artifact path is
/// ignored, any other event leaves one pending signal for the next wait. See continuous-sync.md section 1.
/// </summary>
internal sealed class LocalChangeWatch : IChangeWatch
{
    // v1's directory-watcher buffer (SyncAuthorityLocalMonitor.cs), reused here as the one named value.
    private const int BufferSize = 64 * 1024;

    // No LastAccess: reading a file must not signal a change (continuous-sync.md section 1).
    private const NotifyFilters Filters = NotifyFilters.FileName | NotifyFilters.DirectoryName |
        NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime |
        NotifyFilters.Attributes | NotifyFilters.Security;

    private readonly object _lock = new();
    private readonly string _rootPath;
    private readonly PilotDirectoryIdentity _identity;
    private readonly IReadOnlySet<string> _productOwnedRelative;
    private readonly FileSystemWatcher _watcher;
    private bool _pending = true; // a new watch starts with one catch-up signal
    private Exception? _fault;
    private TaskCompletionSource? _waiter;
    private bool _disposed;

    private LocalChangeWatch(string rootPath, PilotDirectoryIdentity identity,
        IReadOnlySet<string> productOwnedRelative)
    {
        _rootPath = rootPath;
        _identity = identity;
        _productOwnedRelative = productOwnedRelative;
        _watcher = new FileSystemWatcher(rootPath)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = BufferSize,
            NotifyFilter = Filters
        };
        _watcher.Created += (_, item) => OnPath(item.FullPath);
        _watcher.Changed += (_, item) => OnPath(item.FullPath);
        _watcher.Deleted += (_, item) => OnPath(item.FullPath);
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnError;
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>Binds the root to its saved NTFS identity, then watches it; a missing or replaced root throws.</summary>
    public static LocalChangeWatch Open(RootDef root, IReadOnlySet<string> productOwnedRelativePaths)
    {
        ArgumentNullException.ThrowIfNull(root);
        var (volume, fileId) = LocalItems.ParseObjectId(root.RootObjectId);
        var identity = new PilotDirectoryIdentity(volume, fileId);
        // A read guard (LIST_DIRECTORY with delete sharing denied) reserves the exact directory while the watcher is
        // built and enabled, so a delete-and-recreate in that gap cannot be bound silently.
        using (PilotDirectoryLease.OpenDirectoryReadGuard(root.RootPath, identity))
        {
            return new LocalChangeWatch(root.RootPath, identity, productOwnedRelativePaths);
        }
    }

    /// <inheritdoc/>
    public Task ChangedAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(LocalChangeWatch));
            if (_waiter is not null) throw new InvalidOperationException("LocalChangeWatch: a wait is already outstanding.");
            if (_fault is not null) return Task.FromException(_fault);
            if (ct.IsCancellationRequested) return Task.FromCanceled(ct);
            if (_pending)
            {
                _pending = false;
                return Task.CompletedTask;
            }
            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiter = waiter;
            ct.Register(() => Cancel(waiter, ct));
            return waiter.Task;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_lock) _disposed = true;
        _watcher.Dispose();
    }

    /// <summary>Raises the buffer-overflow path directly; its only reader is the forced-error check.</summary>
    internal void RaiseErrorForCheck() => OnError(this, new ErrorEventArgs(new InternalBufferOverflowException()));

    private void OnPath(string fullPath)
    {
        if (!IsExcluded(fullPath)) Signal();
    }

    // A rename is ignored only when both the old and the new name are excluded.
    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (!(IsExcluded(e.OldFullPath) && IsExcluded(e.FullPath))) Signal();
    }

    // Buffer overflow (events may have been lost): always signal, then prove the root still has its saved identity.
    private void OnError(object sender, ErrorEventArgs e)
    {
        Signal();
        try
        {
            using var lease = PilotDirectoryLease.Open(_rootPath, _identity);
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    private bool IsExcluded(string fullPath)
    {
        var relative = LocalItems.RelativeTo(_rootPath, fullPath);
        return relative is null || LocalItems.IsExcluded(relative, _productOwnedRelative);
    }

    // The waiter is cleared and completed in the same locked section. Its continuations never run inline (the source
    // is created with RunContinuationsAsynchronously), so no caller resumes while this lock is held.
    private void Signal()
    {
        lock (_lock)
        {
            if (_disposed) return;
            if (_waiter is null)
            {
                _pending = true;
                return;
            }
            var waiter = _waiter;
            _waiter = null;
            waiter.TrySetResult();
        }
    }

    private void Fail(Exception error)
    {
        lock (_lock)
        {
            if (_disposed) return;
            if (_waiter is null)
            {
                _fault = error;
                return;
            }
            var waiter = _waiter;
            _waiter = null;
            waiter.TrySetException(error);
        }
    }

    // Cancelling never consumes: only a waiter still outstanding is cancelled; a signal it lost to stays completed.
    private void Cancel(TaskCompletionSource waiter, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(_waiter, waiter)) return;
            _waiter = null;
            waiter.TrySetCanceled(ct);
        }
    }
}
