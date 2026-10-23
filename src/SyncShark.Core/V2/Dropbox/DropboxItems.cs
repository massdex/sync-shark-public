using System.Net;

namespace Massdex.SyncShark.V2;

/// <summary>The Dropbox endpoint's item mapping and its single write-error mapping.</summary>
internal static class DropboxItems
{
    /// <summary>Maps one listed provider item to an inventory item (dropbox-inventory.md §0).</summary>
    internal static ItemState ToItem(PilotRemoteEntry entry)
    {
        var kind = entry.Kind == PilotEntryKind.Directory ? ItemKind.Folder : ItemKind.File;
        var id = entry.Id ?? throw new InvalidDataException("The Dropbox item has no stable object identity.");
        return new ItemState(entry.RelativePath, kind, id, kind == ItemKind.File ? entry.Revision : null,
            entry.ContentHash, entry.Size, entry.ClientModifiedUtc ?? entry.ModifiedUtc ?? DateTimeOffset.UnixEpoch,
            (entry.TransferBlockedReason is null && !entry.RequiresExport ? ItemFlags.None : ItemFlags.Unreadable) |
                (entry.RequiresExport ? ItemFlags.RequiresExport : ItemFlags.None),
            DropboxExportFacts.Merge(DropboxNameMapping.Fact(entry.RelativePath),
                entry.ExportAs, entry.ExportOptions, entry.RequiresExport));
    }

    /// <summary>Builds the provider entry a conditional write is conditioned on.</summary>
    internal static PilotRemoteEntry ToEntry(ItemState state) => new(
        state.Path,
        state.Kind == ItemKind.Folder ? PilotEntryKind.Directory : PilotEntryKind.File,
        state.ObjectId, state.VersionId, state.Size, state.Hash, state.Mtime);

    /// <summary>
    /// The one account-access mapping (continuous-sync.md section 5): a Dropbox account-access refusal becomes
    /// <see cref="EndpointAccessException"/>; anything else is returned unchanged.
    /// </summary>
    internal static Exception MapAccessError(Exception exception) => exception switch
    {
        EndpointAccessException => exception,
        _ when DropboxPilotClient.IsAccessRefusal(exception) =>
            new EndpointAccessException(exception.Message, exception),
        _ => exception
    };

    /// <summary>
    /// The only write-error mapping: an unproven effect stays uncertain; a provider conflict means the planned target
    /// changed; an account-access refusal becomes <see cref="EndpointAccessException"/>; everything else is returned
    /// unchanged so the Executor records a failure.
    /// </summary>
    internal static Exception MapWriteError(Exception exception)
    {
        if (exception is ProviderWriteBlockedException or ExpectedTargetMismatchException or ProviderNameRefusedException or
            ProviderWriteThrottledException or UncertainEffectException or EndpointAccessException) return exception;
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            var mapped = MapKnownWriteError(current);
            if (!ReferenceEquals(current, mapped)) return mapped;
            // A definite quota refusal is useful even under the deliberate session-start wrapper.
            // Do not unwrap that wrapper for conflict/not_found/throttle classification.
            if (current is IOException { InnerException: DropboxApiException { WriteErrorTag: "insufficient_space" } quota })
                return MapKnownWriteError(quota);
            // Provider-authored IO wrappers deliberately prevent staging errors becoming target mismatches.
            if (current is not (AggregateException or System.Reflection.TargetInvocationException)) break;
        }
        return exception;
    }

    private static Exception MapKnownWriteError(Exception exception) => exception switch
    {
        ProviderWriteBlockedException or ExpectedTargetMismatchException or ProviderNameRefusedException or
            ProviderWriteThrottledException or UncertainEffectException => exception,
        DropboxEffectUncertainException => new UncertainEffectException(exception.Message, exception),
        DropboxApiException { WriteErrorTag: "too_many_write_operations" } =>
            new ProviderWriteThrottledException(exception.Message, exception),
        DropboxApiException { StatusCode: HttpStatusCode.Conflict, WriteErrorTag: "disallowed_name" } =>
            new ProviderNameRefusedException(exception),
        DropboxApiException { WriteErrorTag: "insufficient_space" } =>
            new ProviderWriteBlockedException("insufficient_space", "Free Dropbox storage or increase its quota, then Retry.", exception),
        DropboxApiException { WriteErrorTag: "no_write_permission" or "operation_suppressed" } denied =>
            new ProviderWriteBlockedException(denied.WriteErrorTag!, "Check write permission for this Dropbox folder, then Retry.", exception),
        DropboxApiException { WriteErrorTag: "restricted_content" } =>
            new ProviderWriteBlockedException("restricted_content", "Dropbox restricted this content. Check its access restrictions, then Retry.", exception),
        DropboxApiException { WriteErrorTag: "malformed_path" } =>
            new ProviderWriteBlockedException("malformed_path", "Check the destination path and folder binding, then Retry.", exception),
        DropboxApiException { WriteErrorTag: "team_folder" } =>
            new ProviderWriteBlockedException("team_folder", "Check the selected team folder and account access, then Retry.", exception),
        DropboxApiException { StatusCode: HttpStatusCode.Conflict, WriteErrorTag: "conflict" or "not_found" } =>
            new ExpectedTargetMismatchException(exception.Message, exception),
        _ => MapAccessError(exception)
    };
}
