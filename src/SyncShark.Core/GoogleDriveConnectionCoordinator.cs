namespace Massdex.SyncShark;

public sealed class GoogleDriveConnectionCoordinator(
    GoogleDriveOAuthEndpoint endpoint,
    IDropboxAuthorizationReceiver receiver,
    IGoogleDriveRefreshTokenStore credentials,
    GoogleDriveConnectionStore connections,
    TimeSpan? authorizationTimeout = null)
{
    private static readonly SemaphoreSlim AuthorizationGate = new(1, 1);

    public async Task<GoogleDriveConnectionBinding> ConnectAsync(Func<Uri, Task> openAuthorizationPage,
        string? connectionId = null, CancellationToken cancellationToken = default)
    {
        if (!GoogleDriveProviderConfiguration.IsLiveProviderBound)
            throw new InvalidOperationException("Google Drive is not configured for this build.");
        if (!await AuthorizationGate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("Finish or cancel the existing Google connection first.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(authorizationTimeout ?? TimeSpan.FromMinutes(5));
        Task<DropboxLoopbackSession>? receive = null;
        GoogleDriveConnectionBinding? existing = null;
        string? newId = null; string? previousCredential = null;
        var hadPrimary = false;
        var credentialWritten = false; var bindingWritten = false;
        try
        {
            hadPrimary = connections.Load() is not null;
            existing = connectionId is null ? null : connections.Load(connectionId) ??
                throw new InvalidOperationException("The selected Google account is no longer retained.");
            // Reuse the product's registered loopback listener identity. A concurrent Dropbox flow
            // owns the port and this receiver fails before opening another browser.
            var redirect = new Uri(DropboxProviderConfiguration.RegisteredRedirectUri);
            var flow = GoogleDriveOAuthEndpoint.CreateFlow();
            receive = receiver.ReceiveAsync(redirect, flow.State, timeout.Token);
            if (receive.IsFaulted || receive.IsCanceled) await receive;
            await openAuthorizationPage(endpoint.BuildAuthorizationUri(redirect, flow)).WaitAsync(timeout.Token);
            var session = await receive;
            var token = await endpoint.ExchangeCodeAsync(redirect, session.Code, flow.Verifier, timeout.Token);
            var account = await endpoint.GetAccountAsync(token.AccessToken, timeout.Token);
            if (existing is not null && existing.PermissionId != account.PermissionId)
                throw new InvalidOperationException("This is a different Google account. Existing pairs and credentials were kept unchanged.");
            if (existing is null && connections.LoadActiveBindings().Any(item => item.PermissionId == account.PermissionId))
                throw new InvalidOperationException("This Google account is already connected. Use Reconnect for that account.");
            var key = GoogleDriveConnectionBinding.CreateAccountKey(account.PermissionId);
            newId = GoogleDriveConnectionBinding.CreateConnectionId(key);
            previousCredential = await credentials.ReadAsync(newId, timeout.Token);
            if (existing is null && previousCredential is not null)
                throw new InvalidOperationException("A protected credential already exists for this Google account. It was not overwritten.");
            var now = DateTimeOffset.UtcNow;
            var binding = new GoogleDriveConnectionBinding(GoogleDriveProviderConfiguration.RegisteredClientId,
                key, account.PermissionId, account.Email, account.DisplayName, token.Scopes,
                WindowsGoogleDriveRefreshTokenStore.AccountTarget(newId), newId,
                existing?.ConnectedUtc ?? now, now, account.StorageQuotaJson,
                SharedDrivesEnabled: existing?.SharedDrivesEnabled ?? false);
            GoogleDriveProviderConfiguration.ValidateConnection(binding);
            credentialWritten = true;
            await credentials.WriteAsync(newId, token.RefreshToken!, timeout.Token);
            bindingWritten = true;
            await connections.SaveAsync(binding, timeout.Token);
            return binding;
        }
        catch (Exception failure)
        {
            try
            {
                if (bindingWritten)
                {
                    if (existing is not null) await connections.SaveAsync(existing, CancellationToken.None);
                    else if (!hadPrimary) await connections.DeleteAsync(CancellationToken.None);
                    else if (newId is not null) await connections.RemoveAdditionalAsync(newId, CancellationToken.None);
                }
                if (credentialWritten && newId is not null)
                {
                    if (previousCredential is null) await credentials.DeleteAsync(newId, CancellationToken.None);
                    else await credentials.WriteAsync(newId, previousCredential, CancellationToken.None);
                }
            }
            catch { throw new InvalidOperationException("Google connection failed and its incomplete local state needs recovery. Existing files were not changed."); }
            if (failure is OperationCanceledException && !cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
                throw new TimeoutException("Google authorization timed out. Existing accounts were kept; you can try again.");
            throw;
        }
        finally
        {
            timeout.Cancel();
            if (receive is not null) { try { await receive; } catch { } }
            AuthorizationGate.Release();
        }
    }
}
