using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Captures connection-local issue mutations and publishes only disposed transaction results.</summary>
internal sealed class IssueFactsPublication
{
    private readonly SqliteConnection _connection;
    private readonly bool _writable;
    private readonly Dictionary<string, IssueFacts> _pairs = new(StringComparer.Ordinal);
    private readonly Dictionary<long, IssueFacts> _rows = [];
    private readonly HashSet<(long Id, long Pair)> _touched = [];

    public IssueFactsPublication(SqliteConnection connection, bool writable)
    {
        _connection = connection;
        _writable = writable;
        if (!writable) return;
        connection.CreateFunction(StateSchema.IssueFactsTouchedFunction, (long id, long pair) =>
        {
            _touched.Add((id, pair));
            return 0;
        });
        using var command = StorageCommand.Create(connection, null, StateSchema.CreateIssueFactsTriggers);
        command.ExecuteNonQuery();
    }

    public IssueFacts Read(string pairId)
    {
        Publish();
        if (_pairs.TryGetValue(pairId, out var facts)) return facts;
        using var command = StorageCommand.Create(_connection, null, StateSchema.SelectIssueFacts,
            StateSchema.ParamPair, pairId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return new IssueFacts(pairId, StateSchema.BeforeFirstPairRow);
        facts = new IssueFacts(pairId, reader.GetInt64(StateSchema.IssueFactPair));
        do
        {
            if (!reader.IsDBNull(StateSchema.IssueId))
            {
                var issue = IssueTable.ReadIssue(reader);
                facts.Replace(issue.Id, issue);
            }
        } while (reader.Read());
        // Read-only connections do not receive the writer connection's temporary-trigger notifications.
        if (_writable)
        {
            _pairs.Add(pairId, facts);
            _rows.Add(facts.PairRow, facts);
        }
        return facts;
    }

    public void Publish()
    {
        if (_touched.Count == 0) return;
        try
        {
            foreach (var (id, pair) in _touched)
            {
                if (!_rows.TryGetValue(pair, out var facts)) continue;
                using var command = StorageCommand.Create(_connection, null, StateSchema.SelectIssueFact,
                    StateSchema.ParamId, id);
                using var reader = command.ExecuteReader();
                var issue = reader.Read() && reader.GetInt64(StateSchema.IssueFactPair) == pair
                    ? IssueTable.ReadIssue(reader) : null;
                facts.Replace(id, issue);
            }
        }
        catch
        {
            foreach (var (_, pair) in _touched)
                if (_rows.Remove(pair, out var facts)) _pairs.Remove(facts.PairId);
            throw;
        }
        finally { _touched.Clear(); }
    }

    public void Remove(string pairId)
    {
        if (_pairs.Remove(pairId, out var facts)) _rows.Remove(facts.PairRow);
    }
}
