using System.Globalization;

namespace Massdex.SyncShark.Engine.Edge.Results;

/// <summary>Section 9.3: ResultExcludePreview (ResultExcludeBlocked when protected content prevents the removal)
/// without --yes; ResultExcludeApplied with it. The applied path comes from the saved pair and needs no preview.</summary>
public sealed record ExcludeResult(PairView Pair, bool Applied, ExcludePreview? Preview) : IEngineCliData
{
    /// <summary>The applied, blocked or preview sentence for the exclusion.</summary>
    public string RenderHuman()
    {
        // The applied exclusion is the newest entry of the saved pair's own list.
        if (Applied) return Text.Get(TextKey.ResultExcludeApplied, (TextArg.Path, Pair.Exclusions[^1].Path));
        var preview = Preview ?? throw new InvalidOperationException();
        if (preview.BlockedByProtected) return Text.Get(TextKey.ResultExcludeBlocked, (TextArg.Path, preview.Path));
        return Text.Get(TextKey.ResultExcludePreview, (TextArg.Path, preview.Path),
            (TextArg.CopyFiles, preview.CopyFiles.ToString(CultureInfo.InvariantCulture)),
            (TextArg.CopyBytes, preview.CopyBytes.ToString(CultureInfo.InvariantCulture)),
            (TextArg.RemoveFiles, preview.RemoveFiles.ToString(CultureInfo.InvariantCulture)),
            (TextArg.RemoveBytes, preview.RemoveBytes.ToString(CultureInfo.InvariantCulture)),
            (TextArg.Side, preview.Side.ToString()), (TextArg.Place, Text.Get(preview.RecoveryPlace)));
    }

    public int ExitCode => (int)SyncCliExitCode.Success;
}
