namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    // Targeted offline entry for the check-error and namespace-gate regressions; no full Dropbox suite needed.
    public static async Task RunFolderDeleteRecoveryChecksAsync(string root)
    {
        if (Directory.Exists(root)) throw new InvalidOperationException("Use a fresh bounded test directory.");
        Directory.CreateDirectory(root);
        _root = root;
        await FolderTreeCheckConflictRecoveryAsync();
        await FolderTreePollAllowsUploadCommitAsync();
    }

    private static async Task FolderTreePollAllowsUploadCommitAsync()
    {
        using var rig = await TreeRigAsync(60);
        var checking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.H.Wire.DeleteBatchAsyncJob = true;
        rig.H.Wire.BeforeDeleteJobCheck = async () =>
        {
            checking.TrySetResult();
            await release.Task;
        };
        var binding = Binding().Normalize();
        var gate = DropboxUploadCommitGate.For(binding.ConnectionId, binding.RootNamespaceId, 1);
        rig.H.Wire.BeforeBuryFolder = () => Require(gate.CurrentCount == 0,
            "folder delete submission still holds the namespace commit gate");
        var deleting = rig.ApplyAsync(rig.Plan());
        try
        {
            await checking.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(!deleting.IsCompleted, "the folder job check is blocked");
            // A distinct writer models an independent pair on the same account/namespace. Its upload must
            // finish, not merely stage bytes, while the folder job's HTTP status response remains blocked.
            var bytes = new byte[] { 1, 2, 3 };
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var content = new MemoryStream(bytes);
            var uploaded = await rig.H.Writer().PutAsync(PutAction("independent.txt", Source("independent.txt", bytes)),
                content, stop.Token).WaitAsync(stop.Token);
            Require(uploaded is not null && !deleting.IsCompleted &&
                    rig.H.Wire.Calls.Any(call => call.EndsWith("upload_session/finish_batch_v2", StringComparison.Ordinal)),
                "an unrelated upload commits before the folder job check is released");
        }
        finally
        {
            release.TrySetResult();
            await deleting;
        }
        var deleted = await deleting;
        Require(deleted.All(result => result.Outcome == Outcome.Done),
            "releasing the status response completes the original folder delete normally");
    }
}
