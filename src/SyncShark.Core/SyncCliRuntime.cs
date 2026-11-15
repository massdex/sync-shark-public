namespace Massdex.SyncShark;

// The direct CLI runtime: when no application owns the product, the CLI takes the same exclusive
// PilotInstanceLock the window takes and drives its own SyncAuthorityRuntimeHost over the current
// production stores. All command semantics live once in SyncCliOperationsBase, so this mode and the
// running-application mode cannot diverge.
public sealed class SyncCliRuntime : SyncCliOperationsBase
{
    public const string Trigger = "CLI";
    public const int PlanItemLimit = 200;

    private readonly PilotInstanceLock _instanceLock;
    private readonly HttpClient _httpClient;
    private readonly SyncAuthorityRuntimeHost _host;
    private int _disposed;

    private SyncCliRuntime(PilotInstanceLock instanceLock, HttpClient httpClient, SyncAuthorityRuntimeHost host)
    {
        _instanceLock = instanceLock;
        _httpClient = httpClient;
        _host = host;
    }

    // The registered production lock. A running window, a running tray runtime or another CLI command
    // holds it, and this mode then fails busy instead of forcing, bypassing or duplicating the runtime.
    public static string ProductionLockPath => SyncSharkRuntimePaths.InstanceLockFile;

    public static SyncCliRuntime AcquireProduction() => Acquire(ProductionLockPath);

    public static SyncCliRuntime Acquire(string lockPath)
    {
        var instanceLock = CliProductLock.Acquire(lockPath);
        var httpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromMinutes(10) };
        try
        {
            var host = new SyncAuthorityRuntimeHost(instanceLock.StateAuthority, httpClient);
            return new SyncCliRuntime(instanceLock, httpClient, host);
        }
        catch
        {
            httpClient.Dispose();
            instanceLock.Dispose();
            throw;
        }
    }

    protected override SyncAuthorityRuntimeHost Host => _host;
    protected override PilotStateAuthority StateAuthority => _instanceLock.StateAuthority;
    protected override bool ApplicationOwnsRuntime => false;
    public override string Mode => "direct";

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // Release the durable runtime first, then the exclusive state lock, so a following command never
        // sees a half-disposed product state.
        try { await base.DisposeAsync(); }
        finally
        {
            _httpClient.Dispose();
            _instanceLock.Dispose();
        }
    }
}
