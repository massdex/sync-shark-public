using System.Text.Json;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    public void ObserveJournal(string pairId, string volume, long serial, ulong journal, long next,
        long maximum, long free, bool gap, DateTimeOffset now, bool deferNotice = false, long allocation = SyncSharkJournalNative.Delta) => InStore(() =>
    {
        SyncSharkJournalNative.Volume(volume);
        var old = ReadJournal(serial);
        var state = JournalSizing.Observe(old, journal, next, now) with { Allocation = Math.Max(allocation, SyncSharkJournalNative.Delta) };
        var target = JournalSizing.Target(state, maximum, free, gap, allocation);
        // Resolve pair identities before opening the write transaction (a removed owner is a cache miss).
        if (state.Requested > 0 && (state.NoticePair is null || PairNo(state.NoticePair) is null))
            state = state with { NoticePair = pairId };
        var presentationNo = RequirePair(pairId);
        var ownerNo = state.NoticePair is { } owner ? PairNo(owner) : null;
        using var transaction = _connection.BeginTransaction();
        // Drive letters can be reused. Retire the previous presentation, never transfer its approval.
        _notices.CloseWhere(transaction, "volume-changed",
            "reason_key LIKE $k AND path=$v " +
            "AND EXISTS(SELECT 1 FROM journal_volume WHERE volume=$v AND serial<>$s);",
            ("$k", JournalSizing.NoticeKey + ":%"), ("$v", volume), ("$s", serial));
        StoreSchema.Execute(_connection, transaction,
            "UPDATE journal_volume SET volume='' WHERE volume=$v AND serial<>$s;", ("$v", volume), ("$s", serial));
        // A single volume-owned row chooses one presentation pair, not one notice per pair.
        var prior = ownerNo is { } number
            ? _notices.ById(number, ShortId.Make("n", state.NoticePair!, "0", RowCodec.Key(volume), JournalSizing.Key(serial)), transaction) : null;
        if (state.Requested > 0 && maximum >= state.Requested && allocation >= SyncSharkJournalNative.Delta)
        {
            if (state.NoticePair is { } resolvedPair && ownerNo is { } resolvedNo)
                _notices.CloseResolved(transaction, resolvedNo,
                    ShortId.Make("n", resolvedPair, "0", RowCodec.Key(volume), JournalSizing.Key(serial)), JournalSizing.Enlarge);
            state = state with { Requested = 0 };
            prior = null;
        }
        if ((target > maximum || allocation < SyncSharkJournalNative.Delta) && !state.Never && (state.NotBefore is null || now >= state.NotBefore) &&
            (prior is null || prior.Value.Closed))
        {
            state = state with { Requested = Math.Max(JournalSizing.Minimum, target), NoticePair = state.Requested > 0 ? state.NoticePair : pairId };
        }
        if (state.Requested > 0 && !deferNotice)
        {
            var presentation = state.NoticePair!;
            _notices.Add(transaction, presentation == pairId ? presentationNo : ownerNo!.Value, new Notice(presentation, 0, NoticeKind.Info, volume,
                JournalCapacityExplanation(volume, state.Requested, state.Allocation),
                JournalSizing.Key(serial)), now.ToUnixTimeMilliseconds());
        }
        StoreSchema.Execute(_connection, transaction,
            "INSERT INTO journal_volume(serial,volume,state_json) VALUES($s,$v,$j) ON CONFLICT(serial) DO UPDATE SET volume=$v,state_json=$j;",
            ("$s", serial), ("$v", volume), ("$j", JsonSerializer.Serialize(state)));
        transaction.Commit();
    });

    private static string JournalCapacityExplanation(string volume, long target, long allocation) =>
        $"Windows' change log on {volume} is too small for the required history window; lost or over-limit history requires a full re-check. " +
        $"To enlarge it, run as administrator: fsutil usn createjournal m={target} a={allocation} {volume}. " +
        "Sync continues. Retry with Enlarge, choose Not now, or Ignore with Never.";

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
            pending.State.Requested != request.State.Requested || pending.State.Allocation != request.State.Allocation) return;
        var state = pending.State;
        using var transaction = _connection.BeginTransaction();
        if (grew)
        {
            state = state with { Requested = 0, NotBefore = null };
            StoreSchema.Execute(_connection, transaction, "UPDATE journal_volume SET state_json=$j WHERE serial=$s;",
                ("$j", JsonSerializer.Serialize(state)), ("$s", request.Serial));
            var id = ShortId.Make("n", state.NoticePair!, "0", RowCodec.Key(request.Volume), JournalSizing.Key(request.Serial));
            _notices.CloseResolved(transaction, RequirePair(state.NoticePair!), id, JournalSizing.Enlarge);
        }
        else
        {
            _notices.Add(transaction, RequirePair(state.NoticePair!), new Notice(state.NoticePair!, 0,
                NoticeKind.Info, request.Volume, explanation +
                " " + JournalCapacityExplanation(request.Volume, state.Requested, state.Allocation),
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
