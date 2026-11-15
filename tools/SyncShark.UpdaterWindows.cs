#if NET8_0_OR_GREATER
#nullable disable
#endif
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

internal static class SyncSharkUpdaterWindows
{
    internal static bool Absent(string path)
    {
        try{File.GetAttributes(path);return false;}
        catch(FileNotFoundException){return true;}
        catch(DirectoryNotFoundException){return true;}
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern SafeFileHandle CreateFile(
        string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool DeviceIoControl(SafeFileHandle file,uint code,
        IntPtr input,uint inputLength,byte[] output,uint outputLength,out uint returned,IntPtr overlapped);

    internal static SafeFileHandle HoldJunction(string path,string exactTarget)
    {
        // Read the junction itself, not its destination. Keep it undeletable until
        // admission/publication completes; no user pointer is changed while high.
        var handle=CreateFile(path,0x80000000,1,IntPtr.Zero,3,0x02200000,IntPtr.Zero);
        if(handle.IsInvalid){handle.Dispose();throw new Win32Exception(Marshal.GetLastWin32Error());}
        try
        {
            var bytes=new byte[16384];uint length;
            SyncSharkUpdaterContract.Require(DeviceIoControl(handle,0x000900a8,IntPtr.Zero,0,bytes,(uint)bytes.Length,out length,IntPtr.Zero) &&
                length>=16 && BitConverter.ToUInt32(bytes,0)==0xa0000003,"Active pointer is not an ordinary directory junction.");
            int offset=BitConverter.ToUInt16(bytes,8),count=BitConverter.ToUInt16(bytes,10);
            SyncSharkUpdaterContract.Require((offset&1)==0 && (count&1)==0 && count>0 && 16L+offset+count<=length,"Invalid active junction target range.");
            string target=Encoding.Unicode.GetString(bytes,16+offset,count);
            SyncSharkUpdaterContract.Require(String.Equals(target,@"\??\"+exactTarget,StringComparison.OrdinalIgnoreCase),"Active pointer differs from the exact transaction predecessor/successor.");
            return handle;
        }
        catch{handle.Dispose();throw;}
    }
}
