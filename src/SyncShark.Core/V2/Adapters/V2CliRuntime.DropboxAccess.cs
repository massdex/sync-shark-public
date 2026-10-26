namespace Massdex.SyncShark.V2;

// The Dropbox CLI seam on the v2 runtime: it reuses the same retained connection and credential stores
// the v1 hook uses, so `--cli dropbox ...` works identically under core v2. It creates no pair, plan,
// token or scope of its own.
public sealed partial class V2CliRuntime : IDropboxCliAccess
{
    private DropboxAccountClients? _dropboxAccounts;
    private HttpClient? _dropboxHttp;
    private readonly SyncTransferRateLimiter _dropboxLimiter = new(new SyncTransferSettings());

    /// <summary>The HTTP handler the Dropbox hook sends through; a test replaces it with a scripted wire.</summary>
    internal Func<HttpMessageHandler> DropboxHandlerFactory { get; set; } =
        () => new HttpClientHandler { AllowAutoRedirect = false };

    /// <summary>The protected credential store the Dropbox hook reads; a test replaces it with a stand-in.</summary>
    internal Func<IDropboxRefreshTokenStore> DropboxCredentialsFactory { get; set; } =
        () => new WindowsDropboxRefreshTokenStore();

    /// <summary>The account-bound Dropbox route clients shared by the Dropbox CLI verbs.</summary>
    public DropboxAccountClients DropboxAccounts
    {
        get
        {
            if (_dropboxAccounts is not null) return _dropboxAccounts;
            var authority = _instanceLock?.StateAuthority ?? throw new SyncCliUnsupportedException(
                DropboxCliCatalog.Message(DropboxCliMessage.StateUnavailable));
            var http = new HttpClient(DropboxHandlerFactory()) { Timeout = DropboxCliSettings.HttpTimeout };
            var credentials = DropboxCredentialsFactory();
            _dropboxHttp = http;
            _dropboxAccounts = new DropboxAccountClients(http, new DropboxConnectionStore(authority),
                credentials, binding => new RefreshingDropboxAccessTokenSource(http, credentials, binding),
                _dropboxLimiter);
            return _dropboxAccounts;
        }
    }

    /// <summary>The folder oversized Dropbox results are spilled to.</summary>
    public string ResultFolder => Path.Combine(_productRoot, DropboxCliSettings.ResultFolderName);

    /// <summary>The Dropbox CLI tunables in effect.</summary>
    public DropboxCliSettings DropboxCliSettings => DropboxCliSettings.Default;
}
