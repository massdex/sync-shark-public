namespace Massdex.SyncShark;

/// <summary>Owns a copy source until its bytes are staged or the copy exits.</summary>
internal sealed class SyncUploadSource(Stream source, Action? beforeUpload = null) : IAsyncDisposable
{
    internal static readonly AsyncLocal<SyncUploadSource?> Current = new();
    private Stream? _source = source;

    /// <summary>Test seam: observes a recheck that passed, immediately before the request is sent.</summary>
    internal static Action? BeforeUploadObserved { get; set; }

    // Recheck after asynchronous queue, account and pacing waits, immediately before sending bytes.
    internal void BeforeUpload()
    {
        beforeUpload?.Invoke();
        BeforeUploadObserved?.Invoke();
    }

    /// <summary>Releases this source only when the staging adapter consumed the same stream.</summary>
    internal ValueTask ReleaseAsync(Stream source) => ReferenceEquals(_source, source)
        ? DisposeAsync() : ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => Interlocked.Exchange(ref _source, null)?.DisposeAsync()
        ?? ValueTask.CompletedTask;
}
