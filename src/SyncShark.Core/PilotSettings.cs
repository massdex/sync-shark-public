using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

public sealed record PilotSettings(
    string LocalRoot,
    uint LocalVolumeSerialNumber,
    ulong LocalRootFileId,
    string DropboxRoot,
    string DropboxFolderId);

public static class SyncSharkRuntimePaths
{
    private static string? _productRootForTest;
    public const string SettingsFileName = "pilot-settings.jsonl";
    public const string ConnectionBindingFileName = "dropbox-connection.jsonl";
    public const string GoogleDriveConnectionFileName = "google-drive-connection.jsonl";
    public const string UiPreferencesFileName = "ui-preferences.jsonl";
    public const string PairSetStateName = "sync-pairs";
    public const string RuntimeStateName = "sync-runtime";
    public const string ProductStateName = "sync-product-state";
    public const string HistoryStateName = "sync-history";
    public const string AutomationStateName = "sync-automation";
    public const string InstanceLockFileName = "instance.lock";
    public const string ReplacementStagingDirectoryName = "replacement-staging";
    public static string InstallationUserRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Massdex", "SyncShark");
    public static string ProductRoot => Volatile.Read(ref _productRootForTest) ?? SyncDataLocation.ProductRoot;
    public static string StateRoot => Path.Combine(ProductRoot, "state");
    // Code and mutable per-user state deliberately have different roots.
    public static string InstalledVersionsRoot => Volatile.Read(ref _productRootForTest) is { } testRoot
        ? Path.Combine(testRoot, "versions") : @"C:\Program Files\Massdex\SyncShark\versions";
    internal static bool HasTestProductRoot => Volatile.Read(ref _productRootForTest) is not null;
    public static bool IsInstalledRuntimeDirectory(string directory)
    {
        var full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        var version = Path.GetFileName(full);
        return System.Text.RegularExpressions.Regex.IsMatch(version, @"^[0-9]+\.[0-9]+\.[0-9]+$",
                   System.Text.RegularExpressions.RegexOptions.CultureInvariant) &&
               string.Equals(full, Path.Combine(InstalledVersionsRoot, version), StringComparison.OrdinalIgnoreCase);
    }
    public static string ReplacementStagingRoot => Path.Combine(ProductRoot, ReplacementStagingDirectoryName);
    public static string SettingsFile => Path.Combine(StateRoot, SettingsFileName);
    public static string ConnectionBindingFile => Path.Combine(StateRoot, ConnectionBindingFileName);
    public static string GoogleDriveConnectionFile => Path.Combine(StateRoot, GoogleDriveConnectionFileName);
    public static string InstanceLockFile => Path.Combine(StateRoot, InstanceLockFileName);

    public static string InstalledExecutablePath => Path.Combine(
        Volatile.Read(ref _productRootForTest) ?? InstallationUserRoot, "active", "SyncShark.exe");

    // One canonical installed-process decision. The GUI Explorer gate and the CLI status
    // surface both read this, so they cannot drift apart. The admission itself is unchanged:
    // the running executable must resolve to the same handle-final path as the active
    // launcher, that launcher must exist, the build must be bound to a frozen revision and
    // the resolved versioned runtime must pass the protected-runtime lease.
    public static bool IsInstalledProcess => IsInstalledProcessFor(Environment.ProcessPath, BuildIdentity.SourceRevision);

    internal static bool IsInstalledProcessFor(string? processPath, string? sourceRevision)
    {
        try
        {
            if (processPath is not { } path || !File.Exists(InstalledExecutablePath) ||
                sourceRevision == "UNBOUND") return false;
            var resolved = ResolveExecutablePath(path);
            if (!string.Equals(resolved, ResolveExecutablePath(InstalledExecutablePath), StringComparison.OrdinalIgnoreCase)) return false;
            using var runtime = PilotDirectoryLease.OpenInstalledRuntime(Path.GetDirectoryName(resolved)!);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    // The single handle-based resolver for this process family. Windows returns the final
    // local DOS path for the opened handle, which is what installed-runtime admission compares.
    public static string ResolveExecutablePath(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new char[32768];
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length) throw new IOException("Executable path could not be resolved.");
        var resolved = new string(buffer, 0, (int)length);
        return NormalizeHandleFinalPath(resolved);
    }

    // Pure normalization of a handle-resolved final path, shared by the real resolver and its
    // tests so the exact same decision is exercised without opening any file or network path.
    // Windows returns a handle-resolved DOS path with the extended-path prefix; every admission
    // comparison uses the same ordinary absolute local form, keeps the resolved target rather
    // than the caller's junction path, and rejects non-local forms.
    internal static string NormalizeHandleFinalPath(string resolved)
    {
        if (resolved.Length < 7 || !resolved.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            !char.IsAsciiLetter(resolved[4]) || resolved[5] != ':' || resolved[6] != '\\')
            throw new IOException("The executable did not resolve to a local drive path.");
        return resolved[4..];
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, char[] path, uint length, uint flags);

    internal static IDisposable OverrideProductRootForTest(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullRoot.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The test product root must remain under the operating-system temporary directory.");
        Directory.CreateDirectory(fullRoot);
        if (Interlocked.CompareExchange(ref _productRootForTest, fullRoot, null) is not null)
            throw new InvalidOperationException("The test product root is already overridden in this process.");
        return new ProductRootOverride(fullRoot);
    }

    private sealed class ProductRootOverride(string expectedRoot) : IDisposable
    {
        private string? _expectedRoot = expectedRoot;

        public void Dispose()
        {
            var expected = Interlocked.Exchange(ref _expectedRoot, null);
            if (expected is null) return;
            if (!string.Equals(Interlocked.CompareExchange(ref _productRootForTest, null, expected), expected,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The test product-root override changed unexpectedly.");
        }
    }
}

public sealed record UiPreferences(string Theme, bool KeepRunningWhenClosed = true,
    bool CompactRows = false, bool NotifyProblems = true, bool NotifyCompleted = false,
    bool NotifyTransfers = true, bool NotificationSound = false,
    bool PauseUntilResumed = false, DateTimeOffset? PauseUntilUtc = null,
    IReadOnlyList<SyncAccountPause>? AccountPauses = null, bool ExplorerIntegrationEnabled = true,
    string? ExplorerRegistrationBeforeJson = null, string? UpdateDeliveryFolder = null,
    string? LastNotifiedUpdateVersion = null, bool AutomaticUpdateChecks = true,
    string? DownloadedUpdateFolder = null, IReadOnlyDictionary<string, string>? PageViewModes = null,
    string? PauseTimeZoneId = null, string StatusIconStyle = "Combined")
{
    // U1 presentation contract: the same persisted status-icon preference under the name the frozen
    // shared contract uses. One stored member keeps every existing reader, saved preference file and
    // CLI settings surface unchanged; this alias is only the shared presentation's vocabulary, so it
    // is deliberately not serialised and never becomes a second source of truth.
    [System.Text.Json.Serialization.JsonIgnore]
    public string? IconSet
    {
        get => StatusIconStyle;
        init => StatusIconStyle = value ?? UiPreferencesStore.DefaultIconSet;
    }
}

public sealed record SyncAccountPause(string ConnectionId, bool UntilResumed, DateTimeOffset? UntilUtc,
    string? TimeZoneId = null)
{
    public bool IsActive => IsActiveAt(DateTimeOffset.UtcNow);

    // The injected clock keeps the scope and expiry decision testable and independent of the
    // ambient clock, while IsActive retains its existing meaning for callers.
    public bool IsActiveAt(DateTimeOffset nowUtc) => PauseSchedule.IsPauseActive(UntilResumed, UntilUtc, nowUtc);
}

public sealed class UiPreferencesStore
{
    public const string SystemTheme = "System";
    public const string LightTheme = "Light";
    public const string AmoledTheme = "AMOLED";
    public const string DarkTheme = "Dark";
    public const string SimpleView = "Simple";
    public const string DetailedView = "Detailed";
    // U1 presentation contract: the three selectable status-icon styles. These are the one canonical
    // spelling of the persisted preference; the shared presentation reads them instead of repeating
    // a private copy of the same three strings.
    public const string ColourIconSet = "Colour";
    public const string SymbolIconSet = "Symbol";
    public const string CombinedIconSet = "Combined";
    public const string DefaultIconSet = CombinedIconSet;
    public static readonly string[] IconSets = [ColourIconSet, SymbolIconSet, CombinedIconSet];

    // One canonical supported-value test: exact, case-sensitive and ordinal like the stored value.
    public static bool IsSupportedIconSet(string? iconSet) =>
        iconSet is not null && Array.IndexOf(IconSets, iconSet) >= 0;

    public static bool IsStatusIconStyle(string? value) => IsSupportedIconSet(value);
    private readonly MigratingMutableJsonStore<UiPreferences> _store;

    public UiPreferencesStore(PilotStateAuthority authority) =>
        _store = new MigratingMutableJsonStore<UiPreferences>(authority,
            SyncSharkRuntimePaths.UiPreferencesFileName);

    public UiPreferences Load() => Normalize(_store.Load() ?? new UiPreferences(SystemTheme));

    public Task SaveAsync(UiPreferences preferences, CancellationToken cancellationToken = default) =>
        _store.SaveAsync(Normalize(preferences), cancellationToken);

    private static UiPreferences Normalize(UiPreferences preferences)
    {
        var theme = preferences.Theme.Trim();
        if (theme is not (SystemTheme or LightTheme or AmoledTheme or DarkTheme))
            throw new InvalidDataException("The saved UI theme preference is not supported.");
        // An absent or unsupported style resolves to the frozen combined default instead of failing
        // the whole preference load. That keeps exactly one deterministic supported style for the
        // shared presentation while never persisting an unrecognised value as authority.
        preferences = preferences with
        {
            StatusIconStyle = IsSupportedIconSet(preferences.StatusIconStyle) ? preferences.StatusIconStyle : DefaultIconSet
        };
        var pauses = (preferences.AccountPauses ?? []).ToArray();
        if (pauses.Any(item => !PauseScopeRules.IsExactConnectionId(item.ConnectionId)) ||
            pauses.Select(item => item.ConnectionId).Distinct().Count() != pauses.Length)
            throw new InvalidDataException(
                "Account pause state is not bound to unique Dropbox or Google Drive connection identities.");
        // A timezone that no longer resolves is dropped rather than failing the whole load; the
        // persisted UTC deadline stays authoritative.
        if (preferences.PauseTimeZoneId is not null && PauseSchedule.TryResolveZone(preferences.PauseTimeZoneId) is null)
            preferences = preferences with { PauseTimeZoneId = null };
        pauses = pauses.Select(item => item.TimeZoneId is not null && PauseSchedule.TryResolveZone(item.TimeZoneId) is null
            ? item with { TimeZoneId = null }
            : item).ToArray();
        var modes = preferences.PageViewModes is null
            ? null
            : preferences.PageViewModes.Where(item => item.Value is SimpleView or DetailedView)
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        if (modes is not null && modes.Keys.Any(key => !System.Text.RegularExpressions.Regex.IsMatch(key, "^[a-z]{3,24}$")))
            throw new InvalidDataException("A saved page view mode is not bound to a supported page key.");
        return preferences with { Theme = theme, AccountPauses = pauses, PageViewModes = modes };
    }
}

public sealed class PilotSettingsStore
{
    private readonly MigratingMutableJsonStore<PilotSettings> _store;

    public PilotSettingsStore(PilotStateAuthority authority) =>
        _store = new MigratingMutableJsonStore<PilotSettings>(authority, SyncSharkRuntimePaths.SettingsFileName);

    public PilotSettings? Load()
    {
        var settings = _store.Load();
        return settings is null ? null : Normalize(settings);
    }

    public Task SaveAsync(PilotSettings settings, CancellationToken cancellationToken = default) =>
        _store.SaveAsync(Normalize(settings), cancellationToken);

    private static PilotSettings Normalize(PilotSettings settings)
    {
        if (settings.LocalVolumeSerialNumber == 0 || settings.LocalRootFileId == 0)
            throw new InvalidDataException("The saved local folder identity is incomplete.");
        if (!PilotPath.IsDropboxFolderIdentity(settings.DropboxFolderId))
            throw new InvalidDataException("The saved Dropbox folder ID is incomplete.");
        return settings with
        {
            LocalRoot = PilotPath.NormalizeLocalRoot(settings.LocalRoot),
            DropboxRoot = PilotPath.NormalizeDropboxRoot(settings.DropboxRoot)
        };
    }
}

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

internal sealed record MutableJsonValue<T>(bool Present, T? Value);

/// <summary>
/// Moves frequently-mutated legacy JSONL state into the existing crash-safe alternating-slot
/// format. The JSONL predecessor is deliberately retained as recovery evidence, but once either
/// successor slot exists it is never consulted again; corrupt successor state therefore cannot
/// silently roll authority back to an old connection or preference.
/// </summary>
internal sealed class MigratingMutableJsonStore<T>
{
    private readonly object _gate = new();
    private readonly AppendOnlyJsonStore<T> _legacy;
    private readonly DualSlotJsonStore<MutableJsonValue<T>> _current;

    public MigratingMutableJsonStore(PilotStateAuthority authority, string legacyFileName)
    {
        if (!legacyFileName.EndsWith(".jsonl", StringComparison.Ordinal) ||
            Path.GetFileName(legacyFileName) != legacyFileName)
            throw new ArgumentException("A legacy JSONL state file name is required.", nameof(legacyFileName));
        _legacy = new AppendOnlyJsonStore<T>(authority, legacyFileName);
        _current = new DualSlotJsonStore<MutableJsonValue<T>>(authority,
            Path.GetFileNameWithoutExtension(legacyFileName));
    }

    public bool Exists => Load() is not null;

    public T? Load()
    {
        lock (_gate)
        {
            var current = _current.Load();
            if (current is not null) return current.Present ? current.Value : default;

            var legacy = _legacy.Load();
            if (legacy is null) return default;
            _current.SaveAsync(new MutableJsonValue<T>(true, legacy)).GetAwaiter().GetResult();
            var verified = _current.Load() ??
                           throw new IOException("Mutable state migration did not produce a readable successor.");
            if (!verified.Present || verified.Value is null)
                throw new IOException("Mutable state migration did not preserve the predecessor value.");
            return verified.Value;
        }
    }

    public Task SaveAsync(T value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _ = LoadCurrentOrLegacyForMigration();
            return _current.SaveAsync(new MutableJsonValue<T>(true, value), cancellationToken);
        }
    }

    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _ = LoadCurrentOrLegacyForMigration();
            return _current.SaveAsync(new MutableJsonValue<T>(false, default), cancellationToken);
        }
    }

    private T? LoadCurrentOrLegacyForMigration()
    {
        var current = _current.Load();
        if (current is not null) return current.Present ? current.Value : default;
        var legacy = _legacy.Load();
        if (legacy is not null)
            _current.SaveAsync(new MutableJsonValue<T>(true, legacy)).GetAwaiter().GetResult();
        return legacy;
    }
}

internal sealed class AppendOnlyJsonStore<T>(PilotStateAuthority authority, string fileName)
{
    private const int SchemaVersion = 1;
    internal const int PhysicalMaximumRecords = 100;
    internal const int PhysicalMaximumBytes = 1024 * 1024;
    internal const int RecoveryRecordReserve = 2;
    internal const int OperationalMaximumRecords = PhysicalMaximumRecords - RecoveryRecordReserve;
    private static readonly JsonSerializerOptions Options = new();

    public bool Exists => authority.StateFileExists(fileName);

    public T? Load()
    {
        using var stream = authority.OpenStateFile(fileName, create: false, write: false);
        if (stream is null) return default;
        authority.VerifyStateFile(stream, fileName);
        var bytes = ReadBounded(stream);
        var analysis = Analyze(bytes);
        authority.VerifyStateFile(stream, fileName);
        return analysis.Current;
    }

    public Task AppendSetAsync(T value, CancellationToken cancellationToken) => AppendAsync("set", value, cancellationToken);

    public Task AppendDeleteAsync(CancellationToken cancellationToken) => AppendAsync("delete", default, cancellationToken);

    private Task AppendAsync(string operation, T? value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = authority.OpenStateFile(fileName, create: true, write: true) ?? throw new IOException("The state file was not created.");
        authority.VerifyStateFile(stream, fileName);
        var existing = ReadBounded(stream);
        var analysis = Analyze(existing);
        var operationLine = SerializeOperationLine(operation, value);

        byte[]? recoveryLine = null;
        if (analysis.InvalidSuffix.Length > 0)
        {
            var originalSha = Convert.ToHexString(SHA256.HashData(existing)).ToLowerInvariant();
            var badSha = Convert.ToHexString(SHA256.HashData(analysis.InvalidSuffix)).ToLowerInvariant();
            var evidence = new RecoveryEvidence(existing.Length, originalSha, analysis.ValidPrefixLength,
                analysis.InvalidSuffix.Length, badSha, Convert.ToBase64String(analysis.InvalidSuffix));
            var evidenceBytes = JsonSerializer.SerializeToUtf8Bytes(evidence, Options);
            var evidenceName = authority.PersistRecoveryEvidence(fileName, originalSha, evidenceBytes);
            var recovery = new RecoveryState(evidenceName, originalSha, analysis.ValidPrefixLength,
                analysis.InvalidSuffix.Length, badSha, operation == "delete" ? "tombstone" : "set");
            var recoveryPayload = JsonSerializer.SerializeToUtf8Bytes(recovery, Options);
            var recoveryRecord = new AppendRecord<T>(SchemaVersion, "recovery", default,
                Convert.ToHexString(SHA256.HashData(recoveryPayload)).ToLowerInvariant(), recovery);
            recoveryLine = JsonSerializer.SerializeToUtf8Bytes(recoveryRecord, Options);
        }

        var expectedRecordCount = recoveryLine is null ? analysis.RecordCount + 1 : RecoveryRecordReserve;
        var recoveryByteReserve = GetRecoveryByteReserve(fileName, operationLine);
        if (recoveryLine is null)
        {
            if (expectedRecordCount > OperationalMaximumRecords)
                throw new IOException("Append-only operational state would consume its two-record recovery reserve.");
            var operationalLength = checked(analysis.ValidPrefixLength + operationLine.Length + 1);
            if (operationalLength > PhysicalMaximumBytes - recoveryByteReserve)
                throw new IOException("Append-only operational state would consume its derived recovery byte reserve.");
        }
        else
        {
            var recoveryLength = checked(recoveryLine.Length + 1 + operationLine.Length + 1);
            if (expectedRecordCount > PhysicalMaximumRecords || recoveryLength > PhysicalMaximumBytes || recoveryLength > recoveryByteReserve)
                throw new IOException("Append-only recovery emission exceeds its structural reserve.");
        }

        authority.VerifyStateFile(stream, fileName);
        if (recoveryLine is not null)
        {
            var successor = new byte[checked(recoveryLine.Length + 1 + operationLine.Length + 1)];
            recoveryLine.CopyTo(successor, 0);
            successor[recoveryLine.Length] = (byte)'\n';
            operationLine.CopyTo(successor, recoveryLine.Length + 1);
            successor[^1] = (byte)'\n';
            stream.Position = 0;
            stream.Write(successor);
            stream.SetLength(successor.Length);
        }
        else
        {
            stream.Position = analysis.ValidPrefixLength;
            stream.Write(operationLine);
            stream.WriteByte((byte)'\n');
            stream.SetLength(stream.Position);
        }
        stream.Flush(true);
        authority.VerifyStateFile(stream, fileName);
        stream.Position = 0;
        var committed = ReadBounded(stream);
        var committedAnalysis = Analyze(committed);
        if (committedAnalysis.InvalidSuffix.Length != 0 || committedAnalysis.ValidPrefixLength != committed.Length ||
            committedAnalysis.RecordCount != expectedRecordCount)
            throw new IOException("Append-only state readback is not a complete valid successor.");
        authority.VerifyStateFile(stream, fileName);
        return Task.CompletedTask;
    }

    private static byte[] ReadBounded(FileStream stream)
    {
        if (stream.Length > PhysicalMaximumBytes) throw new InvalidDataException("Append-only state exceeds its 1 MiB ceiling.");
        stream.Position = 0;
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static Analysis Analyze(byte[] bytes)
    {
        T? current = default;
        var start = 0;
        var validPrefixLength = 0;
        var records = 0;
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] != (byte)'\n') continue;
            if (index == start)
            {
                start = index + 1;
                validPrefixLength = start;
                continue;
            }
            var line = bytes.AsSpan(start, index - start);
            if (line.Length > 0 && line[^1] == (byte)'\r') line = line[..^1];
            if (!TryApply(line, ref current)) break;
            records++;
            if (records > PhysicalMaximumRecords) throw new InvalidDataException("Append-only state exceeds its 100-record ceiling.");
            start = index + 1;
            validPrefixLength = start;
        }
        var invalid = bytes.AsSpan(validPrefixLength).ToArray();
        return new Analysis(current, validPrefixLength, records, invalid);
    }

    private static bool TryApply(ReadOnlySpan<byte> line, ref T? current)
    {
        try
        {
            using var document = JsonDocument.Parse(line.ToArray());
            var record = JsonSerializer.Deserialize<AppendRecord<T>>(line, Options);
            if (record is null || record.SchemaVersion != SchemaVersion || record.Operation is not ("set" or "delete" or "recovery")) return false;
            byte[] payload;
            switch (record.Operation)
            {
                case "set":
                    if (record.Value is null) return false;
                    if (!document.RootElement.TryGetProperty(nameof(AppendRecord<T>.Value), out var value) ||
                        value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                        return false;
                    payload = Encoding.UTF8.GetBytes(value.GetRawText());
                    break;
                case "delete":
                    payload = Encoding.UTF8.GetBytes("delete");
                    break;
                default:
                    if (record.Recovery is null || string.IsNullOrWhiteSpace(record.Recovery.EvidenceFile)) return false;
                    if (!document.RootElement.TryGetProperty(nameof(AppendRecord<T>.Recovery), out var recovery) ||
                        recovery.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                        return false;
                    payload = Encoding.UTF8.GetBytes(recovery.GetRawText());
                    break;
            }
            var claimed = Convert.FromHexString(record.PayloadSha256);
            if (!CryptographicOperations.FixedTimeEquals(claimed, SHA256.HashData(payload))) return false;
            if (record.Operation == "set") current = record.Value;
            if (record.Operation == "delete") current = default;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    internal static byte[] SerializeSetLine(T value) => SerializeOperationLine("set", value);

    internal static int RecoveryByteReserveForSet(string stateFileName, T value) =>
        GetRecoveryByteReserve(stateFileName, SerializeOperationLine("set", value));

    private static byte[] SerializeOperationLine(string operation, T? value)
    {
        var payload = operation == "set" ? JsonSerializer.SerializeToUtf8Bytes(value, Options) : Encoding.UTF8.GetBytes("delete");
        var operationRecord = new AppendRecord<T>(SchemaVersion, operation, value,
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(), null);
        return JsonSerializer.SerializeToUtf8Bytes(operationRecord, Options);
    }

    private static int GetRecoveryByteReserve(string stateFileName, byte[] requestedOperationLine)
    {
        var maximumHash = new string('f', 64);
        var maximumRecovery = new RecoveryState(
            stateFileName + ".recovery-" + maximumHash + ".json",
            maximumHash,
            PhysicalMaximumBytes,
            PhysicalMaximumBytes,
            maximumHash,
            "tombstone");
        var recoveryPayload = JsonSerializer.SerializeToUtf8Bytes(maximumRecovery, Options);
        var recoveryRecord = new AppendRecord<T>(SchemaVersion, "recovery", default,
            Convert.ToHexString(SHA256.HashData(recoveryPayload)).ToLowerInvariant(), maximumRecovery);
        var maximumRecoveryLine = JsonSerializer.SerializeToUtf8Bytes(recoveryRecord, Options);
        var deleteLine = SerializeOperationLine("delete", default);
        return checked(maximumRecoveryLine.Length + 1 + Math.Max(requestedOperationLine.Length, deleteLine.Length) + 1);
    }

    private sealed record AppendRecord<TValue>(int SchemaVersion, string Operation, TValue? Value, string PayloadSha256, RecoveryState? Recovery);
    private sealed record RecoveryState(string EvidenceFile, string OriginalSha256, int ValidPrefixByteLength,
        int BadByteLength, string BadSha256, string Outcome);
    private sealed record RecoveryEvidence(int OriginalByteLength, string OriginalSha256, int ValidPrefixByteLength,
        int BadByteLength, string BadSha256, string BadBytesBase64);
    private sealed record Analysis(T? Current, int ValidPrefixLength, int RecordCount, byte[] InvalidSuffix);
}
