using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Local;

namespace Massdex.SyncShark.Engine.Run;

/// <summary>Generation resources whose host joins feed, volume and operation borrowers before disposal.</summary>
internal sealed class PairGenerationResources : IAsyncDisposable
{
    private HttpClient? _longpollHttp;

    internal PairGenerationResources(string pairId, long generation, EngineSettings settings, PathScope scope,
        DropboxApi api, DropboxListing dropboxListing, LocalListing sourceLocalListing, UsnPaths? paths,
        DropboxFeed feed, IAdmission admission, string accountOrderKey, DropboxSide dropbox,
        HttpClient longpollHttp)
    {
        PairId = pairId;
        Generation = generation;
        Settings = settings;
        Scope = scope;
        Api = api;
        DropboxListing = dropboxListing;
        SourceLocalListing = sourceLocalListing;
        Paths = paths;
        Feed = feed;
        Admission = admission;
        AccountOrderKey = accountOrderKey;
        Dropbox = dropbox;
        _longpollHttp = longpollHttp;
    }

    internal string PairId { get; }
    internal long Generation { get; }
    internal EngineSettings Settings { get; }
    internal PathScope Scope { get; }
    internal DropboxApi Api { get; }
    internal DropboxListing DropboxListing { get; }
    internal LocalListing SourceLocalListing { get; }
    internal UsnPaths? Paths { get; }
    internal DropboxFeed Feed { get; }
    internal IAdmission Admission { get; }
    internal string AccountOrderKey { get; }
    internal DropboxSide Dropbox { get; }

    public ValueTask DisposeAsync()
    {
        // Feed.RunAsync withdraws reconnects in its joined finally; the host joins it before this release.
        Interlocked.Exchange(ref _longpollHttp, null)?.Dispose();
        return ValueTask.CompletedTask;
    }
}
