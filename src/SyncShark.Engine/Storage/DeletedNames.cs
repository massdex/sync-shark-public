using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;

namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Acquisition-local and published Dropbox deletion history, including spent-name masks.</summary>
public sealed class DeletedNames
{
    private const string Pair = "(SELECT id FROM pair WHERE pair_id=$p)";
    private static readonly ConditionalWeakTable<SqliteConnection,ConcurrentDictionary<(string,Guid),string>> Cursors=new();
    internal static void Completed(SqliteConnection db,string pair,Guid scan,string cursor)
    {
        ArgumentException.ThrowIfNullOrEmpty(cursor); Cursors.GetOrCreateValue(db)[(pair,scan)]=cursor;
    }
    internal static void Forget(SqliteConnection db,string pair,Guid scan) => Cursors.GetOrCreateValue(db).TryRemove((pair,scan),out _);
    internal static byte[] Token(string pair,Guid scan) => [..scan.ToByteArray(),..System.Text.Encoding.UTF8.GetBytes(pair)];

    /// <summary>Read exact deletion evidence for a file, respecting its spent mask.</summary>
    public Task<bool> ContainsAsync(SqliteConnection db,string pairId,Guid? scanId,string key,CancellationToken ct) => ContainsAsync(db,pairId,scanId,key,false,ct);

    /// <summary>For a folder also check ancestor tombstones; a spent candidate never regains eligibility.</summary>
    public async Task<bool> ContainsAsync(SqliteConnection db,string pairId,Guid? scanId,string key,bool isFolder,CancellationToken ct)
    {
        var (cursor,owed)=await ReadStateAsync(db,pairId,ct);
        if(scanId is null && (owed || cursor is null)) return false;
        for(var candidate=key;candidate.Length!=0;candidate=PathNames.Parent(candidate))
        {
            using var delta=StorageCommand.Create(db,null,"SELECT spent FROM discovery_deleted_delta WHERE pair_id="+Pair+" AND path_key=$k",("$p",pairId),("$k",candidate));
            if(await delta.ExecuteScalarAsync(ct) is long spent){if(spent==0)return true;if(candidate==key)return false;}
            else
            {
                if(scanId is Guid id){using var scratch=StorageCommand.Create(db,null,"SELECT 1 FROM scan_state.deleted_name WHERE scan_id=$s AND path_key=$k",("$s",Token(pairId,id)),("$k",candidate));if(await scratch.ExecuteScalarAsync(ct)!=null)return true;}
                if(cursor!=null){using var block=StorageCommand.Create(db,null,"SELECT last_key,name_count,rowid FROM discovery_deleted_block WHERE pair_id="+Pair+" AND last_key >= $k ORDER BY last_key LIMIT 1",("$p",pairId),("$k",candidate));using var reader=await block.ExecuteReaderAsync(ct);if(await reader.ReadAsync(ct)){using var bytes=new SqliteBlob(db,"discovery_deleted_block","names",reader.GetInt64(2),true);bool found=false;foreach(var name in DecodeDeletedNames(bytes,reader.GetInt32(1),reader.GetString(0),null))found|=name==candidate;if(found)return true;}}
            }
            if(!isFolder)break;
        }
        return false;
    }
    /// <summary>Read the separate deleted-history cursor and acquisition debt.</summary>
    public async Task<(string? Cursor,bool Owed)> ReadStateAsync(SqliteConnection db,string pairId,CancellationToken ct)
    {
        using var cmd=StorageCommand.Create(db,null,"SELECT deleted_history_cursor,deleted_history_owed FROM pair WHERE pair_id=$p","$p",pairId);
        using var row=await cmd.ExecuteReaderAsync(ct);if(!await row.ReadAsync(ct))throw new InvalidOperationException("Missing pair");return(row.IsDBNull(0)?null:row.GetString(0),row.GetBoolean(1));
    }
    /// <summary>Publish the exact terminal cursor recorded by the completed listing for this pair and scan.</summary>
    public Task PublishAsync(SqliteConnection db,string pairId,Guid scanId,CancellationToken ct) => Cursors.GetOrCreateValue(db).TryGetValue((pairId,scanId),out var cursor)?PublishAsync(db,pairId,scanId,cursor,ct):throw new InvalidOperationException("Incomplete history acquisition");
    /// <summary>Fold only the completed named history acquisition and publish its terminal cursor atomically.</summary>
    public async Task PublishAsync(SqliteConnection db,string pairId,Guid scanId,string terminalCursor,CancellationToken ct)
    {
        if(!Cursors.GetOrCreateValue(db).TryGetValue((pairId,scanId),out var cursor)||cursor!=terminalCursor)throw new InvalidOperationException("History acquisition is incomplete or stale");
        using var tx=db.BeginTransaction();var token=Token(pairId,scanId);
        using var state=StorageCommand.Create(db,tx,"SELECT deleted_history_cursor FROM pair WHERE pair_id=$p","$p",pairId);
        var prior=await state.ExecuteScalarAsync(ct)??throw new InvalidOperationException("Missing pair");
        if(prior is DBNull)
        {
            using var clear=StorageCommand.Create(db,tx,"DELETE FROM discovery_deleted_block WHERE pair_id="+Pair,"$p",pairId);await clear.ExecuteNonQueryAsync(ct);
            string after="";
            while(true)
            {
                using var cmd=StorageCommand.Create(db,tx,"SELECT path_key FROM scan_state.deleted_name WHERE scan_id=$s AND path_key>$a UNION SELECT path_key FROM discovery_deleted_delta WHERE pair_id="+Pair+" AND spent=0 AND path_key>$a ORDER BY path_key LIMIT 5000",("$p",pairId),("$s",token),("$a",after));
                var keys=new List<string>();using(var reader=await cmd.ExecuteReaderAsync(ct))while(await reader.ReadAsync(ct))keys.Add(reader.GetString(0));if(keys.Count==0)break;after=keys[^1];
                using var save=StorageCommand.Create(db,tx,"INSERT INTO discovery_deleted_block VALUES("+Pair+",$a,$n,$b)",("$p",pairId),("$a",after),("$n",keys.Count),("$b",EncodeDeletedNames(keys)));await save.ExecuteNonQueryAsync(ct);
            }
            using var retire=StorageCommand.Create(db,tx,"DELETE FROM discovery_deleted_delta WHERE pair_id="+Pair+" AND spent=0","$p",pairId);await retire.ExecuteNonQueryAsync(ct);
        }
        else{using var merge=StorageCommand.Create(db,tx,"INSERT OR IGNORE INTO discovery_deleted_delta(pair_id,path_key) SELECT "+Pair+",path_key FROM scan_state.deleted_name WHERE scan_id=$s",("$p",pairId),("$s",token));await merge.ExecuteNonQueryAsync(ct);}
        using var publish=StorageCommand.Create(db,tx,"UPDATE pair SET deleted_history_cursor=$c,deleted_history_owed=0 WHERE pair_id=$p",("$p",pairId),("$c",cursor));await publish.ExecuteNonQueryAsync(ct);tx.Commit();Forget(db,pairId,scanId);
    }
    private const int MaximumDeletedKeyBytes=32768*3;
    private static readonly UTF8Encoding HistoryUtf8=new(false,true);
    private const int DeletedChecksumBytes = 4;
    private const uint DeletedChecksumInitial = 1;
    private const uint DeletedChecksumMask = 65535;
    private const uint DeletedChecksumModulus = 65521;
    private const int DeletedChecksumShift = 16;
    private const int DeletedHistoryBatchSize = 5000;
    private const int DeletedLengthContinuation = 128;
    private const int DeletedLengthMask = 127;
    private const int DeletedLengthShift = 7;
    private const int DeletedStreamEnd = -1;
    private const int DeletedZlibMinimumBytes = 6;
    internal static byte[] EncodeDeletedNames(IReadOnlyList<string> keys)
    {
        if (keys.Count is <= 0 or > DeletedHistoryBatchSize) throw new InvalidDataException();
        using var output = new MemoryStream();
        using (var compressed = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        using (var writer = new BinaryWriter(compressed, HistoryUtf8))
        {
            byte[]? previous = null;
            foreach (var key in keys)
            {
                if (HistoryUtf8.GetByteCount(key) is <= 0 or > MaximumDeletedKeyBytes) throw new InvalidDataException();
                var bytes = HistoryUtf8.GetBytes(key);
                ValidateDeletedKey(bytes, previous);
                writer.Write7BitEncodedInt(bytes.Length);
                writer.Write(bytes);
                previous = bytes;
            }
        }
        return output.ToArray();
    }

    // Yield one bounded key at a time, never allocate a corrupt declared length or a decoded block.
    internal static IEnumerable<string> DecodeDeletedNames(Stream input, int count, string lastKey, string? after)
    {
        if (count is <= 0 or > DeletedHistoryBatchSize) throw new InvalidDataException();
        // ZLibStream accepts a missing trailer; verify its Adler-32 explicitly before publishing any restored set.
        if (input.Length < DeletedZlibMinimumBytes) throw new InvalidDataException();
        var start = input.Position;
        input.Seek(-DeletedChecksumBytes, SeekOrigin.End);
        var trailer = new byte[DeletedChecksumBytes];
        try { input.ReadExactly(trailer); }
        catch (EndOfStreamException error) { throw new InvalidDataException(null, error); }
        input.Position = start;
        var expectedChecksum = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(trailer);
        uint checksum = DeletedChecksumInitial;
        using var compressed = new ZLibStream(input, CompressionMode.Decompress, leaveOpen: true);
        using var reader = new BinaryReader(compressed, HistoryUtf8, leaveOpen: true);
        byte[]? previous = after is null ? null : HistoryUtf8.GetBytes(after);
        string? last = null;
        for (var index = 0; index < count; index++)
        {
            int length;
            try { length = reader.Read7BitEncodedInt(); }
            catch (Exception error) when (error is EndOfStreamException or FormatException)
            { throw new InvalidDataException(null, error); }
            if (length is <= 0 or > MaximumDeletedKeyBytes) throw new InvalidDataException();
            var bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new InvalidDataException();
            ValidateDeletedKey(bytes, previous);
            checksum = DeletedChecksum(checksum, bytes);
            try { last = HistoryUtf8.GetString(bytes); }
            catch (DecoderFallbackException error) { throw new InvalidDataException(null, error); }
            previous = bytes;
            yield return last;
        }
        if (last != lastKey || compressed.ReadByte() != DeletedStreamEnd || checksum != expectedChecksum) throw new InvalidDataException();
    }

    private static uint DeletedChecksum(uint checksum, byte[] bytes)
    {
        var a = checksum & DeletedChecksumMask;
        var b = checksum >> DeletedChecksumShift;
        // The persisted length prefix is the canonical BinaryWriter seven-bit encoding.
        var remaining = bytes.Length;
        do
        {
            var value = (byte)(remaining & DeletedLengthMask);
            remaining >>= DeletedLengthShift;
            Add(remaining == 0 ? value : (byte)(value | DeletedLengthContinuation));
        } while (remaining != 0);
        foreach (var value in bytes) Add(value);
        return (b << DeletedChecksumShift) | a;

        void Add(byte value)
        {
            a = (a + value) % DeletedChecksumModulus;
            b = (b + a) % DeletedChecksumModulus;
        }
    }

    private static void ValidateDeletedKey(byte[] key, byte[]? previous)
    {
        if (key.Length is <= 0 or > MaximumDeletedKeyBytes ||
            previous is not null && previous.AsSpan().SequenceCompareTo(key) >= 0)
            throw new InvalidDataException();
    }

}
