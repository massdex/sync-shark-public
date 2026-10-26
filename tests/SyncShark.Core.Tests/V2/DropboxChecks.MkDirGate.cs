namespace Massdex.SyncShark.V2;

// C29: the namespace commit gate admits writes only. A folder-create batch releases it as soon as its launch
// request has been transmitted, so a post-commit readback or an asynchronous job poll never blocks an unrelated
// effect on the same account/namespace, exactly like a delete batch.
internal static partial class DropboxChecks
{
    /// <summary>Runs the folder-create commit-gate fixtures. Safe with an existing bounded test root.</summary>
    public static async Task RunMkDirGateChecksAsync(string root)
    {
        Directory.CreateDirectory(root);
        _root = root;
        await MkDirCommitGateReleasedBeforeReadbackAsync();
        await MkDirCommitGateReleasedBeforeJobPollingAsync();
        Console.WriteLine("--v2-dropbox-mkdir-gate-checks passed.");
    }

    // Two independent clients share one process-wide account/namespace commit gate. A unique binding keeps this
    // fixture's gate at capacity one regardless of what another test bound first, and the held client is stopped
    // in its post-commit root readback; the other client's create must still submit while it is stopped.
    private static async Task MkDirCommitGateReleasedBeforeReadbackAsync()
    {
        var binding = GateBinding("readback");
        using var holding = new Harness(binding);
        using var competing = new Harness(binding);
        // Account verification must see the same isolated identity that keys the shared commit gate.
        holding.Wire.AccountBinding = competing.Wire.AccountBinding = binding;
        var readback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        holding.Wire.RootMetadataHoldAfter = 1;
        holding.Wire.RootMetadataGate = readback.Task;
        var settings = V2Settings.Default with
        {
            MkDirBatchSize = 1, MkDirBatchMaxAge = MkDirFlushGrace, UploadCommitConcurrency = 1,
        };
        var held = new DropboxWriter(Root(), Side.B, holding.Client, Path.Combine(_root, "gate-readback"), isStateDamage: StorageFailureClassifier.IsStateDamage,
            settings).MkDirAsync("held-readback", CancellationToken.None);
        Task<ItemState>? independent = null;
        try
        {
            await WaitForAsync(() => holding.Wire.MetadataPaths.Count(path => path == DropboxWire.RootId) >= 2,
                "the held create reaches its post-commit root readback");
            independent = new DropboxWriter(Root(), Side.B, competing.Client, Path.Combine(_root, "gate-readback-other"), isStateDamage: StorageFailureClassifier.IsStateDamage,
                settings).MkDirAsync("independent-readback", CancellationToken.None);
            var winner = await Task.WhenAny(independent, Task.Delay(TimeSpan.FromSeconds(3)));
            Require(winner == independent,
                "the namespace commit gate is released before the post-commit readback");
        }
        finally { readback.TrySetResult(); }
        Require((await held.WaitAsync(TimeSpan.FromSeconds(30))).Kind == ItemKind.Folder,
            "the held create still completes once its readback is released");
        if (independent is not null)
            Require((await independent.WaitAsync(TimeSpan.FromSeconds(30))).Kind == ItemKind.Folder,
                "the independent create completes instead of waiting behind the readback");
    }

    // The same rule for the asynchronous job path: while the held client polls its accepted job, the other client's
    // create must still submit; polling is a read, not a namespace write.
    private static async Task MkDirCommitGateReleasedBeforeJobPollingAsync()
    {
        var binding = GateBinding("poll");
        using var holding = new Harness(binding);
        using var competing = new Harness(binding);
        // Account verification must see the same isolated identity that keys the shared commit gate.
        holding.Wire.AccountBinding = competing.Wire.AccountBinding = binding;
        holding.Wire.MkDirBatchAsyncJob = true;
        // Enough in-progress answers to outlast the assertion window, bounded by a 6 s natural worst case; the
        // fixture clears the count in its finally so the held job finishes on its next poll.
        holding.Wire.MkDirBatchPendingChecks = 60;
        var settings = V2Settings.Default with
        {
            MkDirBatchSize = 1, MkDirBatchMaxAge = MkDirFlushGrace, UploadCommitConcurrency = 1,
            BatchPollInterval = TimeSpan.FromMilliseconds(100), BatchPollTimeout = TimeSpan.FromSeconds(30),
        };
        var held = new DropboxWriter(Root(), Side.B, holding.Client, Path.Combine(_root, "gate-poll"), isStateDamage: StorageFailureClassifier.IsStateDamage,
            settings).MkDirAsync("held-poll", CancellationToken.None);
        Task<ItemState>? independent = null;
        try
        {
            await WaitForAsync(() => holding.Wire.Calls.Contains("/2/files/create_folder_batch/check"),
                "the accepted create job is being polled");
            independent = new DropboxWriter(Root(), Side.B, competing.Client, Path.Combine(_root, "gate-poll-other"), isStateDamage: StorageFailureClassifier.IsStateDamage,
                settings).MkDirAsync("independent-poll", CancellationToken.None);
            var winner = await Task.WhenAny(independent, Task.Delay(TimeSpan.FromSeconds(3)));
            Require(winner == independent,
                "the namespace commit gate is released before asynchronous job polling");
        }
        finally { holding.Wire.MkDirBatchPendingChecks = 0; }
        Require((await held.WaitAsync(TimeSpan.FromSeconds(30))).Kind == ItemKind.Folder,
            "the polled create still completes after its job answers");
        if (independent is not null)
            Require((await independent.WaitAsync(TimeSpan.FromSeconds(30))).Kind == ItemKind.Folder,
                "the independent create completes instead of waiting behind job polling");
    }

    // A binding whose account/namespace pair no other fixture uses: the commit gate is pinned by its first
    // creation for a namespace, so this fixture owns a fresh capacity-one gate instead of inheriting one.
    private static DropboxConnectionBinding GateBinding(string scope) => new(
        DropboxProviderConfiguration.RegisteredClientId, "gate-account-" + scope, "gate-root-" + scope,
        "gate-home-" + scope, DropboxProviderConfiguration.ContentAccess,
        DropboxProviderConfiguration.RequiredScopes);

    private static async Task WaitForAsync(Func<bool> condition, string label)
    {
        var started = TimeProvider.System.GetTimestamp();
        while (!condition())
        {
            if (TimeProvider.System.GetElapsedTime(started) > TimeSpan.FromSeconds(10))
                throw new InvalidOperationException("Timed out waiting for: " + label);
            await Task.Delay(10);
        }
    }
}
