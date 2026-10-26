using System.Text.Json;

namespace Massdex.SyncShark.DropboxCli;

/// <summary>A minimal runtime that offers only the Dropbox seam, so the group is tested through its own path.</summary>
internal sealed class DropboxCliRuntimeFixture(IDropboxCliAccess access) : ISyncCliRuntime, IDropboxCliAccess
{
    public DropboxAccountClients DropboxAccounts => access.DropboxAccounts;
    public string ResultFolder => access.ResultFolder;
    public DropboxCliSettings DropboxCliSettings => access.DropboxCliSettings;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public Task<SyncCliStatusData> LoadStatusAsync(CancellationToken cancellationToken) => Unavailable<SyncCliStatusData>();
    public Task<SyncCliAccountListData> LoadAccountsAsync(CancellationToken cancellationToken) => Unavailable<SyncCliAccountListData>();
    public Task<SyncCliPairListData> LoadPairsAsync(CancellationToken cancellationToken) => Unavailable<SyncCliPairListData>();
    public Task<SyncCliPairData> LoadPairAsync(string pairId, CancellationToken cancellationToken) => Unavailable<SyncCliPairData>();
    public Task<SyncCliActivityListData> LoadActivityAsync(string? accountId, int limit, int offset,
        CancellationToken cancellationToken, SyncActivityQuery? query = null, string? search = null,
        string? filter = null, string? pairId = null, string? itemType = null, DateTimeOffset? sinceUtc = null) =>
        Unavailable<SyncCliActivityListData>();
    public Task<SyncCliSettingsData> LoadSettingsAsync(CancellationToken cancellationToken) => Unavailable<SyncCliSettingsData>();
    public Task<SyncCliPlanData> LoadPlanAsync(string pairId, CancellationToken cancellationToken,
        int limit = SyncCli.ActivityDefaultLimit, int offset = 0) => Unavailable<SyncCliPlanData>();
    public Task<SyncCliRunData> RunPairAsync(string pairId, Action<string> progress,
        CancellationToken cancellationToken) => Unavailable<SyncCliRunData>();

    private static Task<T> Unavailable<T>() =>
        throw new SyncCliUnsupportedException("This runtime offers only the Dropbox group.");
}

/// <summary>The shared fixture: a recording wire, the real account seam and an output capture.</summary>
internal sealed class DropboxCliHarness : IDisposable
{
    private readonly HttpClient _http;
    private readonly PilotInstanceLock _instance;

    private DropboxCliHarness(string root, DropboxCliSettings settings, PilotInstanceLock instance, HttpClient http,
        DropboxRouteWire wire, DropboxConnectionBinding binding, DropboxAccountClients accounts)
    {
        Root = root;
        Settings = settings;
        _instance = instance;
        _http = http;
        Wire = wire;
        Binding = binding;
        Accounts = accounts;
        Access = new FixtureAccess(accounts, Path.Combine(root, "cli-results"), settings);
    }

    /// <summary>Builds a fresh fixture over a scratch root with a real retained account and wire.</summary>
    internal static async Task<DropboxCliHarness> CreateAsync(string root, DropboxCliSettings? settings = null)
    {
        Directory.CreateDirectory(root);
        var wire = new DropboxRouteWire();
        var http = new HttpClient(wire);
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId, "account-id",
            "root-namespace", "home-namespace", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes).Normalize();
        var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        try
        {
            var connections = new DropboxConnectionStore(instance.StateAuthority);
            await connections.SaveNewAsync(binding);
            var accounts = new DropboxAccountClients(http, connections, new CredentialStore(),
                _ => new SecretDropboxTokenSource(), new SyncTransferRateLimiter(new SyncTransferSettings()));
            return new DropboxCliHarness(root, settings ?? DropboxCliSettings.Default, instance, http, wire, binding,
                accounts);
        }
        catch
        {
            http.Dispose();
            instance.Dispose();
            throw;
        }
    }

    internal DropboxRouteWire Wire { get; }
    internal DropboxConnectionBinding Binding { get; }
    internal DropboxAccountClients Accounts { get; }
    internal IDropboxCliAccess Access { get; }
    internal DropboxCliSettings Settings { get; }
    internal string Root { get; }

    /// <summary>Runs one command through the shared CLI entry point, capturing both streams.</summary>
    internal async Task<(int Exit, string Out, string Err, JsonElement? Data)> RunAsync(params string[] extra)
    {
        var args = new string[extra.Length + 1];
        args[0] = "--cli";
        Array.Copy(extra, 0, args, 1, extra.Length);
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await SyncCli.ExecuteAsync(args, output, error, () => new DropboxCliRuntimeFixture(Access));
        JsonElement? data = null;
        var text = output.ToString();
        if (text.TrimStart().StartsWith('{'))
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.TryGetProperty("data", out var value) &&
                value.ValueKind != JsonValueKind.Null) data = value.Clone();
        }
        return (exit, text, error.ToString(), data);
    }

    public void Dispose()
    {
        _http.Dispose();
        Wire.Dispose();
        _instance.Dispose();
    }

    /// <summary>A protected-credential stand-in that satisfies the real account seam.</summary>
    internal sealed class CredentialStore : IDropboxRefreshTokenStore
    {
        public Task<string?> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("fixture-refresh");
        public Task WriteAsync(string refreshToken, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task DeleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed record FixtureAccess(DropboxAccountClients Accounts, string Folder, DropboxCliSettings Settings)
        : IDropboxCliAccess
    {
        public DropboxAccountClients DropboxAccounts => Accounts;
        public string ResultFolder => Folder;
        public DropboxCliSettings DropboxCliSettings => Settings;
    }
}
