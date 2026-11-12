namespace Massdex.SyncShark.Engine;

/// <summary>Both sides' committed source coverage for one pair generation, minted only after terminal coverage is
/// committed (LIVE 4). It attests actual committed acquisition, never absence of children, an unchanged tree or
/// lasting permission.</summary>
public sealed record LiveCoverage(LocalCatchUp Local, DropboxCatchUp Dropbox)
{
    public SelectionPeer? Peer { get; init; }
}
