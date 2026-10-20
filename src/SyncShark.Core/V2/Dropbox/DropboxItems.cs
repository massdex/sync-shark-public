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
            entry.TransferBlockedReason is null ? ItemFlags.None : ItemFlags.Unreadable,
            DropboxNameMapping.Fact(entry.RelativePath));
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
    internal static Exception MapWriteError(Exception exception) => exception switch
    {
        DropboxEffectUncertainException => new UncertainEffectException(exception.Message, exception),
        DropboxApiException { WriteErrorTag: "too_many_write_operations" } =>
            new ProviderWriteThrottledException(exception.Message, exception),
        DropboxApiException { StatusCode: HttpStatusCode.Conflict, WriteErrorTag: "disallowed_name" } =>
            new ProviderNameRefusedException(exception),
        DropboxApiException { StatusCode: HttpStatusCode.Conflict } =>
            new ExpectedTargetMismatchException(exception.Message, exception),
        _ => MapAccessError(exception)
    };
}
