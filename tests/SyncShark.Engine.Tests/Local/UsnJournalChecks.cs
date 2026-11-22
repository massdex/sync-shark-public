using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Massdex.SyncShark.Engine.Local;

namespace Massdex.SyncShark.Engine.Tests.Local;

/// <summary>File publication is the held file's exact record, even if another file changes on the same volume.</summary>
public static class UsnJournalChecks
{
    public static async Task RunAsync()
    {
        DecodeCases();
        var dir=Directory.CreateTempSubdirectory();
        try
        {
            var file=Path.Combine(dir.FullName,"first"); var other=Path.Combine(dir.FullName,"second");
            var journal=new UsnJournal(); var start=journal.Current(dir.FullName);
            using var held=File.OpenHandle(file,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.Read|FileShare.Delete);
            RandomAccess.Write(held,new byte[]{1},0);
            File.WriteAllBytes(other,[2]);
            var proof=journal.Published(held);
            Check.Require(proof!=null,"No publication record was attributed to the held file");
            File.WriteAllBytes(other,[3]); var end=journal.Current(dir.FullName);
            // An independent per-file query supplies the identity and last USN, never the volume's next USN.
            var (id,usn)=FileRecord(held);
            Check.Require(proof!.NextUsn==usn && proof.JournalId==end.JournalId && usn<end.NextUsn,"Unrelated volume activity was mistaken for the selected file’s publication");
            bool found=false; var position=start;
            while(position.NextUsn<end.NextUsn)
            {
                var page=await journal.ReadAsync(dir.FullName,position,end,SettingsParser.Load().JournalPageBufferBytes,default);
                Check.Require(page.Records!=null && page.ConsumedNextUsn>position.NextUsn && page.ConsumedNextUsn<=end.NextUsn,"Finite read failed to advance within its capture");
                found|=page.Records!.Any(r=>r.Usn==usn && r.FileId==id && (r.Reason&0x80000000)!=0);
                position=position with { NextUsn=page.ConsumedNextUsn };
            }
            Check.Require(found,"The captured page did not contain the held file's publication record");
        }
        finally { dir.Delete(true); }
    }

    private static void DecodeCases()
    {
        static byte[] Page(ulong start,ulong next,ushort major)
        {
            var bytes=new byte[72]; BinaryPrimitives.WriteUInt64LittleEndian(bytes,next);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8),64); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12),major);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32),start); return bytes;
        }
        var first=UsnJournal.Decode(Page(16,128,2),16,256);
        var last=UsnJournal.Decode(Page(128,320,2),first.ConsumedNextUsn,256);
        Check.Require(first.Status==NtfsUsnPageStatus.More && first.ConsumedNextUsn==128 && last.Status==NtfsUsnPageStatus.Complete && last.ConsumedNextUsn==256,"Native padding or continuation crossed finite end");
        var v3=UsnJournal.Decode(Page(ulong.MaxValue,128,3),16,256); var shortPage=UsnJournal.Decode(Page(16,128,2).AsSpan(0,60),16,256);
        Check.Require(v3.Status==NtfsUsnPageStatus.UnsupportedRecord && shortPage.Status==NtfsUsnPageStatus.InvalidRecord && v3.Records==null && shortPage.ConsumedNextUsn==16,"Unsupported or malformed layout was interpreted as a V2 record");
    }

    internal static (ulong File,ulong Usn) FileRecord(SafeFileHandle held)
    {
        var record=new byte[4096];
        Check.Require(DeviceIoControl(held,0x900eb,IntPtr.Zero,0,record,record.Length,out var length,IntPtr.Zero) && length>=60,"No per-file USN record");
        var id=BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(8));
        var usn=BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(24));
        return (id,usn);
    }

    [DllImport("kernel32.dll",SetLastError=true)]
    private static extern bool DeviceIoControl(SafeFileHandle device,uint code,IntPtr input,int inputSize,byte[] output,int outputSize,out int count,IntPtr overlapped);
}
