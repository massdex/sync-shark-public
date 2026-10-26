namespace Massdex.SyncShark.V2;

// The test harness wiring the in-memory wire, the state-store fake and the Dropbox client/inventory.
internal static partial class DropboxChecks
{
    private sealed class Harness : IDisposable
    {
        private readonly HttpClient _http;

        public Harness(DropboxConnectionBinding? binding = null, long? uploadThreshold = null,
            SyncTransferRateLimiter? limiter = null)

        {
            Wire = new DropboxWire();
            Known = new InMemoryStateStore();
            _http = new HttpClient(Wire);
            Client = new DropboxPilotClient(_http, new FixedTokenSource(), "/Pilot", binding ?? Binding(), DropboxWire.RootId,
                transferLimiter: limiter, uploadSessionThresholdBytes: uploadThreshold);

            Inventory = new DropboxInventory(Root(), Side.B, Client, Known, "p1");
        }

        public DropboxWire Wire { get; }
        public InMemoryStateStore Known { get; }
        public DropboxPilotClient Client { get; }
        public DropboxInventory Inventory { get; }

        public DropboxWriter Writer() => Writer(Path.Combine(_root, "spool"));

        public DropboxWriter Writer(string spool) => new(Root(), Side.B, Client, spool, isStateDamage: StorageFailureClassifier.IsStateDamage,
            V2Settings.Default with { UploadBatchSize = 1, DeleteBatchSize = 1, MkDirBatchSize = 1 });

        public void Dispose()
        {
            _http.Dispose();
            Wire.Dispose();
            Known.Dispose();
        }
    }

    private static RootDef Root() => new(RootId, ProviderIds.Dropbox, "acc", "/Pilot", DropboxWire.RootId);

    private static DropboxConnectionBinding Binding() => new(
        DropboxProviderConfiguration.RegisteredClientId, "account-id", "root-namespace", "home-namespace",
        DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes);
}
