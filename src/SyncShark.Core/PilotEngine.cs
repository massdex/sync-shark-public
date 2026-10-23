namespace Massdex.SyncShark;

public sealed class PilotEngine(PilotSettings settings, IPilotRemoteStore remoteStore, PilotRunControl control)
{
    public async Task<PilotRunResult> RunAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, control.StopToken);
        var token = linked.Token;
        var decisions = new List<PilotDecision>();

        try
        {
            using var rootLease = PilotDirectoryLease.Open(settings.LocalRoot,
                new PilotDirectoryIdentity(settings.LocalVolumeSerialNumber, settings.LocalRootFileId));
            await remoteStore.VerifyRootIdentityAsync(token);
            var localScan = await new PilotLocalScanner().ScanAsync(rootLease, cancellationToken: token);
            decisions.AddRange(localScan.Decisions);
            var remote = BuildRemoteIndex(await remoteStore.ListEntriesAsync(token));
            await remoteStore.VerifyRootIdentityAsync(token);

            var paths = localScan.Entries.Keys.Concat(remote.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase).ToArray();

            foreach (var path in paths)
            {
                token.ThrowIfCancellationRequested();
                await control.WaitBeforeNextEffectAsync(token);
                localScan.Entries.TryGetValue(path, out var local);
                remote.TryGetValue(path, out var remoteEntry);

                if (local is { Kind: PilotEntryKind.File } && remoteEntry is { Kind: PilotEntryKind.File })
                {
                    var identical = local.Size == remoteEntry.Size && !string.IsNullOrWhiteSpace(local.ContentHash) &&
                                    string.Equals(local.ContentHash, remoteEntry.ContentHash, StringComparison.OrdinalIgnoreCase);
                    decisions.Add(new PilotDecision(path, identical ? PilotDecisionKind.Identical : PilotDecisionKind.Conflict,
                        identical ? "Byte/content-hash identical; skipped." : "Same name has differing bytes; create-only transfer made no mutation."));
                    continue;
                }

                if (local is null && remoteEntry is { Kind: PilotEntryKind.File })
                {
                    decisions.Add(new PilotDecision(path, PilotDecisionKind.Conflict,
                        "Remote-only file is retained unchanged by the legacy upload-only workflow."));
                    continue;
                }

                if (local is { Kind: PilotEntryKind.File } && remoteEntry is null)
                {
                    await UploadOneAsync(local, rootLease, decisions, token);
                }
            }
        }
        catch (OperationCanceledException) when (control.IsStopRequested)
        {
            AddStopped("<run>", decisions);
        }
        catch (PilotLimitException exception)
        {
            decisions.Clear();
            decisions.Add(new PilotDecision("<run>", PilotDecisionKind.Blocked, exception.Message));
        }

        return new PilotRunResult(decisions);
    }

    private async Task UploadOneAsync(PilotLocalEntry local, PilotDirectoryLease rootLease, ICollection<PilotDecision> decisions, CancellationToken token)
    {
        var accepted = false;
        try
        {
            rootLease.VerifyBinding();
            await remoteStore.VerifyRootIdentityAsync(token);
            await using var source = await PilotLocalScanner.OpenValidatedForUploadAsync(local, rootLease, token);
            await remoteStore.VerifyRootIdentityAsync(token);
            var result = await remoteStore.UploadCreateOnlyAsync(local.RelativePath, source, local.ContentHash!, token);
            accepted = true;
            await PilotLocalScanner.VerifyHeldSourceAfterUploadAsync(source, local, rootLease, token);
            if (result.Size != local.Size || !string.Equals(result.ContentHash, local.ContentHash, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(result.FileId) || string.IsNullOrWhiteSpace(result.Revision))
                throw new IOException("Dropbox returned incomplete or mismatched upload metadata.");
            decisions.Add(new PilotDecision(local.RelativePath, PilotDecisionKind.Uploaded,
                "Created in the exact Dropbox folder ID without overwrite or autorename; held source reverified."));
        }
        catch (OperationCanceledException) when (control.IsStopRequested)
        {
            decisions.Add(new PilotDecision(local.RelativePath, PilotDecisionKind.Stopped,
                "Stop interrupted an active create-only request; rerun inventory because Dropbox may already contain the file."));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DropboxApiException)
        {
            var prefix = exception is DropboxEffectUncertainException
                ? "Dropbox acknowledgement was interrupted; the create may or may not exist and must be resolved by rerun: "
                : accepted
                ? "Dropbox accepted the create-only upload, but post-effect verification needs review: "
                : "Create-only upload stopped before an accepted effect: ";
            decisions.Add(new PilotDecision(local.RelativePath, PilotDecisionKind.Blocked, prefix + SafeMessage(exception)));
        }
    }

    private static Dictionary<string, PilotRemoteEntry> BuildRemoteIndex(IEnumerable<PilotRemoteEntry> entries)
    {
        var result = new Dictionary<string, PilotRemoteEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in entries)
        {
            if (!Enum.IsDefined(raw.Kind)) throw new PilotLimitException("Dropbox inventory contains an unsupported entry kind.");
            if (raw.Size < 0) throw new PilotLimitException("Dropbox inventory contains a negative file size.");
            if (raw.Kind == PilotEntryKind.File &&
                (string.IsNullOrWhiteSpace(raw.Id) || string.IsNullOrWhiteSpace(raw.Revision) ||
                 string.IsNullOrWhiteSpace(raw.ContentHash)))
                throw new PilotLimitException("Dropbox inventory contains an incomplete file identity.");
            var path = PilotPath.NormalizeRelative(raw.RelativePath);
            if (result.TryGetValue(path, out var existing))
                throw new PilotLimitException(string.Equals(existing.RelativePath, path, StringComparison.Ordinal)
                    ? "Dropbox inventory contains a duplicate path."
                    : "Case-colliding Dropbox names are not accepted.");
            result[path] = raw with { RelativePath = path };
        }
        return result;
    }

    private static void AddStopped(string path, ICollection<PilotDecision> decisions)
    {
        if (!decisions.Any(item => item.Kind == PilotDecisionKind.Stopped))
            decisions.Add(new PilotDecision(path, PilotDecisionKind.Stopped, "Run stopped before the next effect."));
    }

    private static string SafeMessage(Exception exception) => exception is DropboxApiException api ? api.SafeReason : exception.Message;
}
