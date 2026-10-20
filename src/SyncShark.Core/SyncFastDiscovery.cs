namespace Massdex.SyncShark;

public sealed record SyncDiscoveryCheckResult(string PairName, bool Ready, int MetadataEntries,
    TimeSpan Elapsed, string Status);

public static class SyncFastDiscovery
{
    public static bool IsInstalled => SyncDiscoveryClient.IsInstalled;
    public static string Status => !IsInstalled ? "Not Set Up; Ordinary Scanning Is Active" : SyncDiscoveryClient.Status;
    public static Task RefreshAfterSetupAsync() => SyncDiscoveryClient.ResetAfterSetupAsync();
}

public sealed partial class SyncAuthorityRuntimeHost
{
    /// <summary>Checks the existing protected metadata path without planning or running a sync.</summary>
    public async Task<SyncDiscoveryCheckResult> CheckFastDiscoveryAsync(string pairId,
        CancellationToken cancellationToken = default)
    {
        stateAuthority.VerifyAuthority();
        var retained = await _authority.LoadPairSetAsync(cancellationToken);
        var pair = retained.Pairs.SingleOrDefault(item =>
            string.Equals(item.Id, pairId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Select a saved folder pair before checking Discovery.");
        if (pair.FilesOnDemandEnabled)
            return new(pair.Name, false, 0, TimeSpan.Zero,
                "This Pair Uses Windows Files On Demand; Fast Discovery Is Not Required.");
        using var root = PilotDirectoryLease.Open(pair.LocalRoot,
            new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = await SyncDiscoveryClient.TryReadAsync(
            [new(root.Path, root.Identity.VolumeSerialNumber, root.Identity.FileId)], cancellationToken);
        root.VerifyBinding();
        stateAuthority.VerifyAuthority();
        return new(pair.Name, result is not null, result?.Roots.Sum(view => view.Items.Count) ?? 0,
            started.Elapsed, SyncFastDiscovery.Status);
    }
}
