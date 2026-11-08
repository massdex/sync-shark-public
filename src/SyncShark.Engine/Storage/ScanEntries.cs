using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Applies listing events to scan rows for one side, and records a discovery scan's deleted names, within one
/// caller-owned transaction.</summary>
internal sealed class ScanEntries : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string _pairId;
    private readonly byte[] _scanId;
    private readonly Side _side;
    private readonly bool _begunEmpty;
    private readonly bool _liveOverrides;
    // Set only for a discovery scan's Dropbox writer: no other scan has the deleted-name relation.
    private readonly long? _deletedNamePairRow;
    private readonly long? _deletedDeltaPairRow;
    private readonly Dictionary<string, SqliteCommand> _commands = new();
    private SqliteCommand? _descendantProbe;
    private List<string>? _deletedNames;

    internal ScanEntries(SqliteConnection connection, string pairId, Side side, bool begunEmpty,
        long? deletedNamePairRow = null, bool liveOverrides = false, long? deletedDeltaPairRow = null, byte[]? scanId = null)
    {
        _connection = connection;
        _pairId = pairId;
        _scanId = scanId ?? Guid.Empty.ToByteArray();
        _side = side;
        _begunEmpty = begunEmpty;
        _liveOverrides = liveOverrides;
        _deletedNamePairRow = deletedNamePairRow;
        _deletedDeltaPairRow = deletedDeltaPairRow;
    }

    internal void ApplyBatch(SqliteTransaction transaction, List<Listed> batch)
    {
        for (var index = 0; index < batch.Count;)
        {
            if (batch[index].Entry is not null)
            {
                Apply(transaction, batch[index++]);
                continue;
            }
            var deleted = new List<(Listed Item, string Key)>();
            do
            {
                var item = batch[index++];
                deleted.Add((item, PathNames.Key(item.Path)));
            } while (index < batch.Count && batch[index].Entry is null);
            // Ordinal order keeps related keys adjacent; a live event must never move across a tombstone.
            deleted.Sort(static (left, right) => string.CompareOrdinal(left.Key, right.Key));
            foreach (var item in deleted) Apply(transaction, item.Item, item.Key);
        }
    }

    // One event application: the delete and upsert branches share the caller transaction and cached commands.
    internal void Apply(SqliteTransaction transaction, Listed item, string? ownKey = null)
    {
        ownKey ??= PathNames.Key(item.Path);
        if (item.Entry is not { } entry)
        {
            // Every deleted name counts, including one with no live row left to remove below.
            if (_deletedNamePairRow is not null || _deletedDeltaPairRow is not null)
                (_deletedNames ??= new()).Add(ownKey);
            using var command = new SqliteCommand(StateSchema.ScanDeleteSelf, _connection, transaction);
            command.Parameters.AddWithValue(StateSchema.ParamPair, _pairId);
            command.Parameters.AddWithValue(StateSchema.ParamScan, _scanId);
            command.Parameters.AddWithValue(StateSchema.ParamSide, (int)_side);
            command.Parameters.AddWithValue(StateSchema.ParamTargetKey, ownKey);
            // Tombstones can arrive after children without their parent, or after a kind change.
            if (command.ExecuteScalar() is not long hasDescendants || hasDescendants != 0)
                Execute(transaction, StateSchema.ScanDeleteDescendants, (StateSchema.ParamTargetKey, ownKey));
            return;
        }
        if (_liveOverrides && _deletedNamePairRow is not null)
        {
            SaveDeletedNames(transaction);
            Execute(transaction, StateSchema.DeletedNameLive,
                (StateSchema.ParamPairRow, _deletedNamePairRow.Value), (StateSchema.ParamPath, ownKey));
        }
        // A scan that began empty can still hold children from earlier batches: only a probe decides (#129).
        if (!entry.IsFolder && (!_begunEmpty || HasDescendants(transaction, ownKey)))
        {
            Execute(transaction, StateSchema.ScanDeleteDescendants, (StateSchema.ParamTargetKey, ownKey));
        }
        Execute(transaction, StateSchema.ScanUpsert,
            (StateSchema.ParamJoinKey, PathNames.KeyFor(_side, item.Path, ownKey)),
            (StateSchema.ParamOwnKey, ownKey),
            (StateSchema.ParamPath, item.Path),
            (StateSchema.ParamIsFolder, entry.IsFolder),
            (StateSchema.ParamSize, entry.Size),
            (StateSchema.ParamMtime, entry.Modified.UtcTicks),
            (StateSchema.ParamId, StateEncoding.IdFor(_side, entry.Id)),
            (StateSchema.ParamRev, entry.Revision),
            (StateSchema.ParamHash, StateEncoding.Hash(entry.ContentHash)),
            (StateSchema.ParamSkip, entry.Skip));
    }

    internal void SaveDeletedNames(SqliteTransaction transaction)
    {
        if (_deletedNames is null) return;
        // Ordinal avoids encoding allocations; only supplementary versus U+E000–U+FFFF keys lose BINARY locality, never set correctness.
        _deletedNames.Sort(StringComparer.Ordinal);
        foreach (var key in _deletedNames)
        {
            if (_deletedDeltaPairRow is { } deltaPair)
                Execute(transaction, StateSchema.AppendDeletedHistory,
                    (StateSchema.ParamPairRow, deltaPair), (StateSchema.ParamPath, key));
            else if (_deletedNamePairRow is { } pair)
                Execute(transaction, StateSchema.DeletedNameInsert,
                    (StateSchema.ParamPairRow, pair), (StateSchema.ParamPath, key));
        }
        _deletedNames = null;
    }

    public void Dispose()
    {
        _descendantProbe?.Dispose();
        _descendantProbe = null;
        foreach (var command in _commands.Values) command.Dispose();
        _commands.Clear();
    }

    // The probe is cached for this transaction exactly like _commands, so the fresh path builds no command per event;
    // binding the key still allocates.
    private bool HasDescendants(SqliteTransaction transaction, string ownKey)
    {
        var command = _descendantProbe;
        if (command is null)
        {
            command = new SqliteCommand(StateSchema.ScanHasDescendants, _connection, transaction);
            command.Parameters.AddWithValue(StateSchema.ParamScan, _scanId);
            command.Parameters.AddWithValue(StateSchema.ParamPair, _pairId);
            command.Parameters.AddWithValue(StateSchema.ParamSide, (int)_side);
            command.Parameters.AddWithValue(StateSchema.ParamTargetKey, ownKey);
            _descendantProbe = command;
        }
        else
        {
            command.Parameters[StateSchema.ParamTargetKey].Value = ownKey;
        }
        return command.ExecuteScalar() is long found && found != 0;
    }

    private void Execute(SqliteTransaction transaction, string sql, params (string Name, object? Value)[] arguments)
    {
        if (!_commands.TryGetValue(sql, out var command))
        {
            command = new SqliteCommand(sql, _connection, transaction);
            _commands.Add(sql, command);
            command.Parameters.AddWithValue(StateSchema.ParamPair, _pairId);
            command.Parameters.AddWithValue(StateSchema.ParamScan, _scanId);
            command.Parameters.AddWithValue(StateSchema.ParamSide, (int)_side);
            foreach (var (name, value) in arguments) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        else
        {
            foreach (var (name, value) in arguments) command.Parameters[name].Value = value ?? DBNull.Value;
        }
        command.ExecuteNonQuery();
    }
}
