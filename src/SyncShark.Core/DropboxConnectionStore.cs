namespace Massdex.SyncShark;

public sealed class DropboxConnectionStore
{
    private readonly MigratingMutableJsonStore<DropboxConnectionBinding> _store;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> WriteGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _writeGate;

    public DropboxConnectionStore(PilotStateAuthority authority)
    {
        _writeGate = WriteGates.GetOrAdd(Path.GetFullPath(authority.StateRoot), _ => new SemaphoreSlim(1, 1));
        _store = new MigratingMutableJsonStore<DropboxConnectionBinding>(authority,
            SyncSharkRuntimePaths.ConnectionBindingFileName);
    }

    public bool Exists => _store.Exists;

    public DropboxConnectionBinding? Load()
    {
        var binding = _store.Load()?.Normalize();
        if (binding is not null) DropboxProviderConfiguration.ValidateConnection(binding);
        return binding;
    }

    public IReadOnlyList<DropboxConnectionBinding> LoadActiveBindings()
    {
        var primary = Load();
        if (primary is null) return [];
        if (primary.CredentialReference != WindowsDropboxRefreshTokenStore.RegisteredTarget ||
            primary.ConnectionId != DropboxConnectionBinding.CreateConnectionId(primary.AccountId))
            throw new InvalidDataException("Primary Dropbox credential is not bound to its original account target.");
        var result = new List<DropboxConnectionBinding> { primary };
        var ids = new HashSet<string>(StringComparer.Ordinal) { primary.ConnectionId };
        var accounts = new HashSet<string>(StringComparer.Ordinal) { primary.AccountId };
        foreach (var raw in primary.AdditionalConnections ?? [])
        {
            if (raw.AdditionalConnections?.Count > 0 || raw.RetainedConnections?.Count > 0)
                throw new InvalidDataException("Nested connection state is not permitted.");
            var binding = raw.Normalize(); DropboxProviderConfiguration.ValidateConnection(binding);
            if (!ids.Add(binding.ConnectionId) || !accounts.Add(binding.AccountId) ||
                binding.ConnectionId != DropboxConnectionBinding.CreateConnectionId(binding.AccountId) ||
                binding.CredentialReference != WindowsDropboxRefreshTokenStore.AccountTarget(binding.ConnectionId))
                throw new InvalidDataException("Duplicate or incorrectly bound Dropbox account.");
            result.Add(binding);
        }
        return result;
    }

    public DropboxConnectionBinding? Load(string? connectionId) => string.IsNullOrWhiteSpace(connectionId)
        ? Load() : LoadActiveBindings().SingleOrDefault(binding => binding.ConnectionId == connectionId);

    public IReadOnlyList<ProviderConnectionRecord> LoadConnections()
    {
        var binding = Load();
        if (binding is null) return [];
        var active = LoadActiveBindings().Select(item => item.ToProviderRecord(active: true)).ToArray();
        return (binding.RetainedConnections ?? [])
            .Where(item => !active.Any(current => current.ConnectionId == item.ConnectionId))
            .Concat(active)
            .Select(ProviderConnectionRecord.Normalize)
            .OrderByDescending(item => item.IsActive)
            .ThenBy(item => item.Provider, StringComparer.Ordinal)
            .ThenBy(item => item.ConnectionId, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task SaveInertConnectionAsync(ProviderConnectionRecord connection,
        CancellationToken cancellationToken = default)
    {
        connection = ProviderConnectionRecord.Normalize(connection);
        if (connection.IsActive)
            throw new InvalidOperationException(
                "Unsupported provider connections may be retained only as inactive records in this release.");
        var active = Load() ?? throw new InvalidOperationException(
            "An active Dropbox connection must exist before retaining future-provider records.");
        if (string.Equals(connection.ConnectionId, active.ConnectionId, StringComparison.Ordinal))
            throw new InvalidDataException("An inert record cannot replace the active connection identity.");
        var retained = (active.RetainedConnections ?? [])
            .Where(item => !string.Equals(item.ConnectionId, connection.ConnectionId, StringComparison.Ordinal))
            .Append(connection)
            .ToArray();
        await SaveAsync(active with { RetainedConnections = retained }, cancellationToken);
    }

    public async Task SaveNewAsync(DropboxConnectionBinding binding, CancellationToken cancellationToken = default)
    {
        DropboxProviderConfiguration.ValidateConnection(binding);
        if (Load() is not null) throw new IOException("A Dropbox connection is already persisted.");
        await _store.SaveAsync(binding, cancellationToken);
    }

    public async Task SaveAsync(DropboxConnectionBinding binding, CancellationToken cancellationToken = default)
    {
        binding = binding.Normalize();
        DropboxProviderConfiguration.ValidateConnection(binding);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var primary = Load();
            if (primary is null || primary.ConnectionId == binding.ConnectionId)
            {
                if (primary is not null && (primary.AccountId != binding.AccountId || primary.RootNamespaceId != binding.RootNamespaceId || primary.HomeNamespaceId != binding.HomeNamespaceId))
                    throw new InvalidDataException("An existing connection cannot be reassigned to a different account or namespace.");
                await _store.SaveAsync(binding with { AdditionalConnections = primary?.AdditionalConnections ?? binding.AdditionalConnections }, cancellationToken);
                return;
            }
            var current = LoadActiveBindings();
            if (binding.ConnectionId != DropboxConnectionBinding.CreateConnectionId(binding.AccountId) ||
                binding.CredentialReference != WindowsDropboxRefreshTokenStore.AccountTarget(binding.ConnectionId) ||
                current.Any(item => item.AccountId == binding.AccountId && item.ConnectionId != binding.ConnectionId))
                throw new InvalidDataException("Another account's credential or connection cannot be reused.");
            var additional = current.Skip(1).Where(item => item.ConnectionId != binding.ConnectionId)
                .Append(binding with { AdditionalConnections = null, RetainedConnections = null }).ToArray();
            await _store.SaveAsync(primary with { AdditionalConnections = additional }, cancellationToken);
        }
        finally { _writeGate.Release(); }
    }

    public async Task RemoveAdditionalAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var primary = Load() ?? throw new InvalidOperationException("No connection is retained.");
            if (primary.ConnectionId == connectionId) throw new InvalidOperationException("The primary connection cannot be removed as an additional account.");
            await _store.SaveAsync(primary with { AdditionalConnections = (primary.AdditionalConnections ?? [])
                .Where(item => item.ConnectionId != connectionId).ToArray() }, cancellationToken);
        }
        finally { _writeGate.Release(); }
    }

    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        if (_store.Exists) await _store.DeleteAsync(cancellationToken);
    }
}
