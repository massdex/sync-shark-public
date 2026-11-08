using System.Text.Json;
using Massdex.SyncShark.V2;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Reads legacy configuration and baselines from one read-only snapshot.</summary>
public static class PairImport
{
    /// <summary>Reads configuration without retaining an open source snapshot.</summary>
    public static (PairConfig Pair, bool Paused) Read(string productRoot, string v2PairId,
        EngineSettings settings, IStateStore engineStore)
    {
        using var source = Open(productRoot, v2PairId, settings, engineStore);
        return (source.Pair, source.Paused);
    }

    /// <summary>Keeps configuration and streamed baselines in the same source transaction.</summary>
    public static Snapshot Open(string productRoot, string v2PairId, EngineSettings settings, IStateStore engineStore)
    {
        if (engineStore.Pair(v2PairId) is not null)
            throw new EngineRequestException(TextKey.ImportExists, (TextArg.Pair, v2PairId));
        SqliteConnection? connection = null;
        SqliteTransaction? transaction = null;
        try
        {
            connection = StateStore.Connect(SqliteStateStore.DatabasePath(productRoot), SqliteOpenMode.ReadOnly);
            transaction = connection.BeginTransaction(deferred: true);
            using var command = StorageCommand.Create(connection, transaction, StateSchema.SelectV2Pair,
                StateSchema.ParamPair, v2PairId);
            var definition = command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<PairDef>(json) : null;
            if (definition is null)
                throw new EngineRequestException(TextKey.ImportMissing, (TextArg.Pair, v2PairId));
            using var pause = StorageCommand.Create(connection, transaction, StateSchema.SelectLegacyPairPaused,
                StateSchema.ParamPair, v2PairId);
            var paused = (long)pause.ExecuteScalar()! != 0;
            var local = definition.A.Provider == ProviderIds.Local ? definition.A : definition.B;
            var dropbox = definition.A.Provider == ProviderIds.Dropbox ? definition.A : definition.B;
            if (local.Provider != ProviderIds.Local || dropbox.Provider != ProviderIds.Dropbox ||
                dropbox.AccountId is not { } accountId)
                throw new EngineRequestException(TextKey.ImportUnsupported, (TextArg.Pair, v2PairId));
            var pair = new PairConfig(v2PairId, definition.Name, local.RootPath, dropbox.RootPath, accountId,
                definition.IgnoreRules,
                definition.Excluded.Select(path => new Exclusion(Side.Local, path, Keep: true)).ToList(),
                settings.DefaultFirstRunPolicy, definition.Enabled && !paused, null);
            return new Snapshot(connection, transaction, pair, paused, local == definition.A, local.RootId,
                local.RootObjectId, dropbox.RootObjectId);
        }
        catch (Exception error)
        {
            transaction?.Dispose();
            connection?.Dispose();
            if (error is SqliteException sqlite)
                throw sqlite.SqliteErrorCode == SQLitePCL.raw.SQLITE_CANTOPEN
                    ? new EngineRequestException(TextKey.ImportMissing, (TextArg.Pair, v2PairId))
                    : new EngineRequestException(TextKey.ImportReadFailed, (TextArg.Pair, v2PairId));
            throw;
        }
    }

    /// <summary>A source snapshot must stay open until the destination transaction has consumed its rows.</summary>
    public sealed class Snapshot : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly SqliteTransaction _transaction;
        private readonly bool _localA;
        private readonly string _localRoot;
        private readonly string _localRootIdentity;
        private readonly string _dropboxRootIdentity;
        private bool _read;
        public PairConfig Pair { get; }
        public bool Paused { get; }
        public BaselineImportCounts Count { get; private set; } = new(0, 0, 0);

        internal Snapshot(SqliteConnection connection, SqliteTransaction transaction, PairConfig pair,
            bool paused, bool localA, string localRoot, string localRootIdentity, string dropboxRootIdentity)
        {
            _connection = connection;
            _transaction = transaction;
            Pair = pair;
            Paused = paused;
            _localA = localA;
            _localRoot = localRoot;
            _localRootIdentity = localRootIdentity;
            _dropboxRootIdentity = dropboxRootIdentity;
        }

        /// <summary>Checks the old roots through native readers before saving any imported authority.</summary>
        public async Task ImportAsync(IStateStore store, Func<CancellationToken, Task<RootBinding>> readRoots,
            CancellationToken ct = default)
        {
            (uint Volume, ulong FileId) expected;
            try
            {
                expected = LocalItems.ParseObjectId(_localRootIdentity);
                if (string.IsNullOrWhiteSpace(_dropboxRootIdentity)) throw new ArgumentException();
            }
            catch (ArgumentException) { throw new EngineRequestException(TextKey.ImportUnsupported, (TextArg.Pair, Pair.PairId)); }
            RootBinding observed;
            try { observed = await readRoots(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { throw new EngineRequestException(TextKey.ImportReadFailed, (TextArg.Pair, Pair.PairId)); }
            if (!NtfsIdentityPaths.MatchesLegacyIdentity(observed.Local, expected.Volume, expected.FileId) ||
                observed.Dropbox.FolderId != _dropboxRootIdentity)
                throw new EngineRequestException(TextKey.ImportUnsupported, (TextArg.Pair, Pair.PairId));
            ct.ThrowIfCancellationRequested();
            try { store.SaveImportedPair(Pair, Paused, Baselines(ct)); }
            catch (SqliteException) { throw new EngineRequestException(TextKey.ImportReadFailed, (TextArg.Pair, Pair.PairId)); }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or FormatException)
            { throw new EngineRequestException(TextKey.ImportUnsupported, (TextArg.Pair, Pair.PairId)); }
        }

        /// <summary>Streams all rows; only recreated-folder duplicates are reduced to one current identity.</summary>
        public IEnumerable<SyncedEntry> Baselines(CancellationToken cancellationToken = default)
        {
            using var rows = ReadBaselines(cancellationToken).GetEnumerator();
            while (true)
            {
                bool next;
                try { next = rows.MoveNext(); }
                catch (SqliteException) { throw new EngineRequestException(TextKey.ImportReadFailed, (TextArg.Pair, Pair.PairId)); }
                catch (Exception error) when (error is ArgumentException or InvalidCastException or FormatException or OverflowException)
                { throw new EngineRequestException(TextKey.ImportUnsupported, (TextArg.Pair, Pair.PairId)); }
                if (!next) yield break;
                yield return rows.Current;
            }
        }

        private IEnumerable<SyncedEntry> ReadBaselines(CancellationToken cancellationToken)
        {
            if (_read) throw new InvalidOperationException();
            _read = true;
            using var command = StorageCommand.Create(_connection, _transaction, StateSchema.SelectV2Baselines,
                (StateSchema.ParamPair, Pair.PairId), (StateSchema.ParamLocalSideA, _localA));
            using var reader = command.ExecuteReader();
            var group = new List<LegacyRow>();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = Project(reader);
                if (group.Count != 0 && group[0].Entry.LocalKey != row.Entry.LocalKey)
                {
                    yield return Select(group);
                    group.Clear();
                }
                group.Add(row);
                Count = Count with { SourceRows = Count.SourceRows + 1 };
            }
            if (group.Count != 0) yield return Select(group);
            cancellationToken.ThrowIfCancellationRequested();
        }

        private SyncedEntry Select(List<LegacyRow> group)
        {
            var selected = group[0];
            if (group.Count > 1)
            {
                if (group.Any(row => !row.Entry.IsFolder || row.Entry.LocalPath != selected.Entry.LocalPath ||
                    row.Entry.DropboxPath != selected.Entry.DropboxPath || row.Entry.DropboxId != selected.Entry.DropboxId))
                    throw new EngineRequestException(TextKey.ImportUnsupported, (TextArg.Pair, Pair.PairId));
                LegacyRow? current = null;
                var matches = 0;
                foreach (var row in group)
                {
                    using var exists = StorageCommand.Create(_connection, _transaction, StateSchema.V2LocalIdentityExists,
                        (StateSchema.ParamRoot, _localRoot), (StateSchema.ParamIdentity, row.LocalObjectId),
                        (StateSchema.ParamPath, row.Entry.LocalPath));
                    if ((long)exists.ExecuteScalar()! != 0) { current = row; matches++; }
                }
                selected = matches == 1 ? current! : group.MaxBy(row => row.Generation)!;
            }
            Count = Count with { ImportedRows = Count.ImportedRows + 1,
                DiscardedFolderRows = Count.DiscardedFolderRows + group.Count - 1 };
            return selected.Entry;
        }

        private LegacyRow Project(SqliteDataReader reader)
        {
            var localPath = reader.GetString(StateSchema.V2BaselineLocalPath);
            var dropboxPath = reader.GetString(StateSchema.V2BaselineDropboxPath);
            var kind = (ItemKind)reader.GetInt64(StateSchema.V2BaselineKind);
            if (kind is not (ItemKind.File or ItemKind.Folder))
                throw new EngineRequestException(TextKey.ImportUnsupported, (TextArg.Pair, Pair.PairId));
            var localObjectId = reader.GetString(StateSchema.V2BaselineLocalId);
            var (_, fileId) = LocalItems.ParseObjectId(localObjectId);
            var hash = kind == ItemKind.Folder ? string.Empty
                : RowCodec.HashHex((byte[])reader.GetValue(StateSchema.V2BaselineHash))!;
            return new LegacyRow(new SyncedEntry(PathNames.Key(localPath), localPath,
                PathNames.DropboxKey(dropboxPath), dropboxPath, kind == ItemKind.Folder,
                LocalIdentity.FileIdText(fileId), kind == ItemKind.Folder ? 0 : reader.GetInt64(StateSchema.V2BaselineSize),
                kind == ItemKind.Folder ? 0 : reader.GetInt64(StateSchema.V2BaselineLocalMtime),
                reader.GetString(StateSchema.V2BaselineDropboxId), hash),
                reader.GetInt64(StateSchema.V2BaselineGeneration), localObjectId);
        }

        public void Dispose()
        {
            _transaction.Dispose();
            _connection.Dispose();
        }

        private sealed record LegacyRow(SyncedEntry Entry, long Generation, string LocalObjectId);
    }
}
