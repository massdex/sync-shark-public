namespace Massdex.SyncShark.Engine.Edge;

/// <summary>Commands operations for the edge module.</summary>
public sealed class Commands
{
    /// <summary>Translate the registered command to its Host operation; render the returned recovery place in the exclusion preview without probing the drive. For refused pair removal render ResultPairNotRemoved with the returned reason and its remedy, without another lookup.</summary>
    public Task<int> ExecuteAsync(string[] args, Run.Host host, TextWriter output, CancellationToken ct) => throw new NotImplementedException();

    /// <summary>ExecuteAsync for the selected Commands operation.</summary>
    public Task<int> ExecuteAsync(string[] args, Run.Host host, TextWriter output, TextWriter error, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>RenderPair for the selected Commands operation.</summary>
    public static string RenderPair(PairConfig pair) => throw new NotImplementedException();
    /// <summary>RenderPairs for the selected Commands operation.</summary>
    public static string RenderPairs(IReadOnlyList<PairConfig> pairs) => throw new NotImplementedException();
    /// <summary>RenderExclusionApplied for the selected Commands operation.</summary>
    public static string RenderExclusionApplied(string path) => throw new NotImplementedException();
    /// <summary>RenderExclusionBlocked for the selected Commands operation.</summary>
    public static string RenderExclusionBlocked(string path) => throw new NotImplementedException();
    /// <summary>RenderExclusionPreview for the selected Commands operation.</summary>
    public static string RenderExclusionPreview(string path, Side side, long copyFiles, long copyBytes, long removeFiles, long removeBytes, string recoveryPlace) => throw new NotImplementedException();
    /// <summary>RenderPairRemoved for the selected Commands operation.</summary>
    public static string RenderPairRemoved(string pairId) => throw new NotImplementedException();
    /// <summary>RenderPairNotRemoved for the selected Commands operation.</summary>
    public static string RenderPairNotRemoved(string pairId, TextKey cause) => throw new NotImplementedException();
    /// <summary>RenderIgnores for the selected Commands operation.</summary>
    public static string RenderIgnores(string pairId, IReadOnlyList<string> rules) => throw new NotImplementedException();
    /// <summary>RenderNotices for the selected Commands operation.</summary>
    public static string RenderNotices(IReadOnlyList<Notice> notices) => throw new NotImplementedException();
}
