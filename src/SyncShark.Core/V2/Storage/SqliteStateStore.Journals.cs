using System.Text.Json;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    public void ObserveJournal(string pairId, string volume, long serial, ulong journal, long next,
        long maximum, long free, bool gap, DateTimeOffset now, bool deferNotice = false) => InStore(() =>
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
            state = state with { Requested = target, NoticePair = state.Requested > 0 ? state.NoticePair : pairId };
            if (!deferNotice)
            {
                var presentation = state.NoticePair!;
                _notices.Add(transaction, RequirePair(presentation), new Notice(presentation, 0, NoticeKind.Info, volume,
                $"Windows' change history on {volume} is too small; enlarge to {target / (double)JournalSizing.Minimum:0.##} GB. Sync continues, using a full scan when history is missing.",
                JournalSizing.Key(serial)), now.ToUnixTimeMilliseconds());
            }
        }
        StoreSchema.Execute(_connection, transaction,
            "INSERT INTO journal_volume(serial,volume,state_json) VALUES($s,$v,$j) ON CONFLICT(serial) DO UPDATE SET volume=$v,state_json=$j;",
            ("$s", serial), ("$v", volume), ("$j", JsonSerializer.Serialize(state)));
        transaction.Commit();
    });

    internal sealed record JournalPending(long Serial, string Volume, JournalSizingState State);

    internal JournalPending? PendingJournal(long serial) => InStore(() =>
    {
        var state = ReadJournal(serial);
        if (state is null || state.Never || state.Requested <= 0 || state.NoticePair is null ||
            PairNo(state.NoticePair) is null) return null;
        var volume = StoreSchema.Scalar(_connection, null, "SELECT volume FROM journal_volume WHERE serial=$s;", ("$s", serial)) as string;
        return string.IsNullOrEmpty(volume) ? null : new JournalPending(serial, volume, state);
    });

    internal void FinishSilentJournal(JournalPending request, bool grew, string explanation) => InStore(() =>
    {
        // An attended choice or drive replacement can win while maintenance is in flight.
        var pending = PendingJournal(request.Serial);
        if (pending is null || pending.Volume != request.Volume || pending.State.NoticePair != request.State.NoticePair ||
            pending.State.Requested != request.State.Requested) return;
        var state = pending.State;
        using var transaction = _connection.BeginTransaction();
        if (grew)
        {
            state = state with { Requested = 0, NotBefore = _clock.GetUtcNow().AddDays(1) };
            StoreSchema.Execute(_connection, transaction, "UPDATE journal_volume SET state_json=$j WHERE serial=$s;",
                ("$j", JsonSerializer.Serialize(state)), ("$s", request.Serial));
            var id = ShortId.Make("n", state.NoticePair!, "0", RowCodec.Key(request.Volume), JournalSizing.Key(request.Serial));
            _notices.CloseResolved(transaction, RequirePair(state.NoticePair!), id, JournalSizing.Enlarge);
        }
        else
        {
            _notices.Add(transaction, RequirePair(state.NoticePair!), new Notice(state.NoticePair!, 0,
                NoticeKind.Info, request.Volume, explanation +
                " Sync continues with ordinary scanning. Retry with Enlarge (UAC), choose Not now, or Ignore with Never.",
                JournalSizing.Key(request.Serial)), _clock.GetUtcNow().ToUnixTimeMilliseconds());
        }
        transaction.Commit();
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
