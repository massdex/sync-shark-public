namespace Massdex.SyncShark;

/// <summary>
/// The one download path both <c>get</c>/<c>thumbnail</c> and <c>dropbox api</c> use: stream the route to a
/// new temporary beside --out, verify any returned content hash, then move it onto --out.
/// </summary>
public static class DropboxCliDownload
{
    /// <summary>
    /// Streams one download route to a temporary beside <paramref name="outPath"/>, verifies a returned
    /// content hash (a route that returns none is allowed and written unverified), and moves the bytes onto
    /// <paramref name="outPath"/> only after the check succeeds. Cleanup failures are recorded in
    /// <paramref name="warnings"/>.
    /// </summary>
    public static async Task<DropboxCliDownloadResult> RunAsync(DropboxCliRouteRunner runner, DropboxRoute route,
        string argument, string? namespaceId, string outPath, bool yes, DropboxCliWarnings warnings,
        CancellationToken cancellationToken)
    {
        FileStream? temporary = null;
        try
        {
            temporary = DropboxCliLocalFiles.CreateTemporary(outPath);
            using var result = await runner.CallAsync(
                new DropboxRouteCall(route, argument, namespaceId, ContentOut: temporary), cancellationToken);
            var metadata = result.Result.RootElement.Clone();
            var bytes = result.ContentBytes;
            var contentHash = DropboxCliOutput.Str(metadata, "content_hash");
            var verified = false;
            if (contentHash is null)
            {
                runner.Note(DropboxCliMessage.DownloadUnverified, ("route", route.Name));
            }
            else
            {
                temporary.Position = 0;
                var hash = await DropboxContentHash.ComputeAsync(temporary, cancellationToken);
                verified = string.Equals(hash, contentHash, StringComparison.OrdinalIgnoreCase);
                if (!verified)
                    throw new SyncCliException("verification_failed",
                        DropboxCliCatalog.Message(DropboxCliMessage.DownloadHashMismatch), SyncCliExitCode.Failed);
            }
            DropboxCliLocalFiles.CommitOutput(outPath, temporary!, yes, warnings);
            return new DropboxCliDownloadResult(metadata, bytes, contentHash, verified);
        }
        catch
        {
            DropboxCliLocalFiles.DiscardTemporary(temporary, warnings);
            throw;
        }
    }
}
