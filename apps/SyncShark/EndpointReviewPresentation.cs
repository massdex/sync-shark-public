namespace Massdex.SyncShark;

/// <summary>
/// Presentation of the two ordered endpoints behind a live review. Retained engine field names
/// are compatibility ports, not promises that a side is local or belongs to Dropbox.
/// </summary>
internal sealed record EndpointReviewPresentation(SyncEndpointContext Context)
{
    internal static EndpointReviewPresentation For(SyncPair pair) => new(SyncEndpointContext.Capture(pair));
    internal bool LeftUsesLocalPort => Context.Left.CompatibilityPort == SyncAuthorityCloudSides.LogicalLeft;
    internal string LeftTitle => Title(Context.Left);
    internal string RightTitle => Title(Context.Right);
    internal string LocalPortTitle => LeftUsesLocalPort ? LeftTitle : RightTitle;
    internal string RemotePortTitle => LeftUsesLocalPort ? RightTitle : LeftTitle;
    internal T LeftValue<T>(T local, T remote) => LeftUsesLocalPort ? local : remote;
    internal T RightValue<T>(T local, T remote) => LeftUsesLocalPort ? remote : local;
    internal SyncAuthorityConflictResolutionKind Keep(SyncPairSide side) =>
        (side == SyncPairSide.Left ? Context.Left : Context.Right).CompatibilityPort == SyncAuthorityCloudSides.LogicalLeft
            ? SyncAuthorityConflictResolutionKind.KeepLocal
            : SyncAuthorityConflictResolutionKind.KeepDropbox;
    internal static string Title(SyncEndpointContextSide side) =>
        SyncEndpointPresentation.SectionTitle(side.Side, side.Kind);
    internal static string Location(SyncEndpointContextSide side) =>
        (side.IsLocal ? side.LocalPath : side.FolderPath ?? side.FolderId) ?? "Not recorded";

    /// <summary>
    /// The bulk conflict action caption. The side is an ordered position, never a provider, so the
    /// caption is exactly "Use left" or "Use right" and a Google Drive or cloud-left endpoint is never
    /// labelled local or Dropbox. The internal resolution handler keeps its existing compatibility name.
    /// </summary>
    internal static string ConflictBulkLabel(SyncPairSide side) =>
        "Use " + SyncEndpointPresentation.SideLabel(side).ToLowerInvariant();

    /// <summary>
    /// The accessible name of a bulk conflict action. It names the pair's ACTUAL ordered endpoint
    /// identities through the same captured context the comparison and first-sync surfaces already use,
    /// and it always states that the action retains the OPPOSITE endpoint's version. With no single pair
    /// resolved (several pairs selected) it stays a neutral ordered-side statement.
    /// </summary>
    internal static string ConflictBulkAccessibleName(SyncPairSide side, EndpointReviewPresentation? presentation)
    {
        var mine = SyncEndpointPresentation.SideLabel(side).ToLowerInvariant();
        var other = SyncEndpointPresentation.SideLabel(Opposite(side)).ToLowerInvariant();
        if (presentation is null)
            return $"Use the {mine} endpoint's version for the selected conflicts. The {other} endpoint's version is " +
                   "retained in Recovery first.";
        return $"Use the {mine} endpoint's version for the selected conflicts: {Descriptor(presentation, side)}. " +
               $"The {other} endpoint's version, {Descriptor(presentation, Opposite(side))}, is retained in Recovery first.";
    }

    /// <summary>The ordered endpoint this pair position names.</summary>
    internal SyncEndpointContextSide Side(SyncPairSide side) => side == SyncPairSide.Left ? Context.Left : Context.Right;

    /// <summary>The other ordered position.</summary>
    internal static SyncPairSide Opposite(SyncPairSide side) =>
        side == SyncPairSide.Left ? SyncPairSide.Right : SyncPairSide.Left;

    /// <summary>One ordered endpoint as "Left — Google Drive at /folder/".</summary>
    private static string Descriptor(EndpointReviewPresentation presentation, SyncPairSide side) =>
        Title(presentation.Side(side)) + " at " + Location(presentation.Side(side));
}
