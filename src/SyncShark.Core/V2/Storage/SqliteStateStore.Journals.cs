using System.Text.Json;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    public void ObserveJournal(string pairId, string volume, long serial, ulong journal, long next,
        long maximum, long free, bool gap, DateTimeOffset now) => InStore(() =>
    {
        SyncSharkJournalNative.Volume(volume);
        var old = ReadJournal(serial);
        var state = JournalSizing.Observe(old, journal, next, now);
        var target = JournalSizing.Target(state, maximum, free, gap);
        using var transaction = _connection.BeginTransaction();
        // Drive letters can be reused. Retire the previous presentation, never transfer its approval.
        StoreSchema.Execute(_connection, transaction,
            "UPDATE notice SET state='closed',choice='volume-changed' WHERE reason_key LIKE $k AND path=$v " +
            "AND EXISTS(SELECT 1 FROM journal_volume WHERE volume=$v AND serial<>$s);",
            ("$k", JournalSizing.NoticeKey + ":%"), ("$v", volume), ("$s", serial));
        StoreSchema.Execute(_connection, transaction,
            "UPDATE journal_volume SET volume='' WHERE volume=$v AND serial<>$s;", ("$v", volume), ("$s", serial));
        // A single volume-owned row chooses one presentation pair, not one notice per pair.
        var prior = state.NoticePair is { } owner && PairNo(owner) is { } ownerNo
            ? _notices.ById(ownerNo, ShortId.Make("n", owner, "0", RowCodec.Key(volume), JournalSizing.Key(serial)), transaction) : null;
        if (target > maximum && !state.Never && (state.NotBefore is null || now >= state.NotBefore) &&
            (prior is null || prior.Value.Closed))
        {
            state = state with { Requested = target, NoticePair = pairId };
            _notices.Add(transaction, RequirePair(pairId), new Notice(pairId, 0, NoticeKind.Info, volume,
                $"Windows' change history on {volume} is too small; enlarge to {target / (double)JournalSizing.Minimum:0.##} GB. Sync continues, using a full scan when history is missing.",
                JournalSizing.Key(serial)), now.ToUnixTimeMilliseconds());
        }
        StoreSchema.Execute(_connection, transaction,
            "INSERT INTO journal_volume(serial,volume,state_json) VALUES($s,$v,$j) ON CONFLICT(serial) DO UPDATE SET volume=$v,state_json=$j;",
            ("$s", serial), ("$v", volume), ("$j", JsonSerializer.Serialize(state)));
        transaction.Commit();
    });

    internal Notice? PendingJournal(long serial) => InStore(() =>
    {
        var state = ReadJournal(serial);
        if (state is null || state.Never || state.Requested <= 0 || state.NoticePair is null ||
            PairNo(state.NoticePair) is not { } pairNo) return null;
        var volume = StoreSchema.Scalar(_connection, null, "SELECT volume FROM journal_volume WHERE serial=$s;", ("$s", serial)) as string;
        if (string.IsNullOrEmpty(volume)) return null;
        var notice = _notices.ById(pairNo, ShortId.Make("n", state.NoticePair, "0", RowCodec.Key(volume), JournalSizing.Key(serial)));
        return notice is not null && !notice.Value.Closed ? notice.Value.Notice : null;
    });

    private JournalSizingState? ReadJournal(long serial) =>
        StoreSchema.Scalar(_connection, null, "SELECT state_json FROM journal_volume WHERE serial=$s;", ("$s", serial)) is string json
            ? JsonSerializer.Deserialize<JournalSizingState>(json) : null;

    public (long Serial, JournalSizingState State) JournalRequest(Notice notice) => InStore(() =>
    {
        if (notice.NoticeId is null || PairNo(notice.PairId) is not { } pairNo)
            throw new InvalidOperationException("The journal notice is no longer open.");
        var saved = _notices.ById(pairNo, notice.NoticeId);
        if (saved is null || saved.Value.Closed || !JournalSizing.IsNotice(saved.Value.Notice) ||
            saved.Value.Notice.Path != notice.Path)
            throw new InvalidOperationException("The journal notice is no longer open.");
        using var command = StoreSchema.Command(_connection, null,
            "SELECT serial,state_json FROM journal_volume WHERE volume=$v;");
        command.Parameters.AddWithValue("$v", notice.Path);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var state = JsonSerializer.Deserialize<JournalSizingState>(reader.GetString(1))!;
            if (notice.ReasonKey == JournalSizing.Key(reader.GetInt64(0)) && state.NoticePair == notice.PairId && state.Requested > 0 && !state.Never)
                return (reader.GetInt64(0), state);
        }
        throw new InvalidOperationException("The volume changed. Refresh journal notices.");
    });

    public void CompleteJournalChoice(Notice notice, string choice) => InStore(() =>
    {
        var (serial, state) = JournalRequest(notice);
        state = state with { Never = choice == JournalSizing.Never, Requested = 0,
            NotBefore = _clock.GetUtcNow().AddDays(choice == JournalSizing.NotNow ? 7 : 1) };
        using var transaction = _connection.BeginTransaction();
        StoreSchema.Execute(_connection, transaction, "UPDATE journal_volume SET state_json=$j WHERE serial=$s;",
            ("$j", JsonSerializer.Serialize(state)), ("$s", serial));
        _notices.CloseResolved(transaction, RequirePair(notice.PairId), notice.NoticeId!, choice);
        transaction.Commit();
    });
}
