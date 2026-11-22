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
            var record=new byte[4096];
            Check.Require(DeviceIoControl(held,0x900eb,IntPtr.Zero,0,record,record.Length,out var length,IntPtr.Zero) && length>=60,"No per-file USN record");
            var id=BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(8));
            var usn=BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(24));
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

    [DllImport("kernel32.dll",SetLastError=true)]
    private static extern bool DeviceIoControl(SafeFileHandle device,uint code,IntPtr input,int inputSize,byte[] output,int outputSize,out int count,IntPtr overlapped);
}
