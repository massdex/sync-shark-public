namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Staged Dropbox content (section 5.4): a single upload not yet sent, or a finished upload session.
/// Dispose does nothing; an unfinished session expires at Dropbox.</summary>
public sealed class DropboxStaged : IStaged
{
    private DropboxStaged(string path, Stream? content, string? sessionId, long total, Entry source, string? contentHash) =>
        (Path, Content, SessionId, Total, Source, ContentHash) = (path, content, sessionId, total, source, contentHash);

    /// <summary>Stages a whole stream for one direct upload.</summary>
    public static DropboxStaged Single(string path, Stream content, Entry source) =>
        new(path, content, null, content.Length, source, null);

    /// <summary>Stages an already appended upload session.</summary>
    public static DropboxStaged Session(string path, string sessionId, long total, Entry source, string contentHash) =>
        new(path, null, sessionId, total, source, contentHash);

    /// <summary>The pair-relative path the content is for.</summary>
    public string Path { get; }
    public Entry Source { get; }
    /// <summary>Set for a single upload.</summary>
    public Stream? Content { get; }
    /// <summary>Direct uploads read the source at publication; a session has already read it.</summary>
    public bool ConsumesSourceOnPublish => Content is not null;
    /// <summary>Set for a session.</summary>
    public string? SessionId { get; }
    /// <summary>The full content length in bytes.</summary>
    public long Total { get; }
    /// <summary>The completed content hash of a session.</summary>
    public string? ContentHash { get; }
    /// <summary>Does nothing: an unfinished upload session expires at Dropbox.</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
