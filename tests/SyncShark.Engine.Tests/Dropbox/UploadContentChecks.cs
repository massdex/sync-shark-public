namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Contract checks for UploadContent.</summary>
public static class UploadContentChecks
{
    /// <summary>Check upload session append across the provider modules.</summary>
    public static Task AppendAsync() => throw new NotImplementedException();
    /// <summary>Check upload replacement order through the module interface.</summary>
    public static Task ReplacementOrderAsync() => throw new NotImplementedException();
    /// <summary>Verifies the UploadContent contract through the module surface.</summary>
    public static Task RunAsync() => throw new NotImplementedException();
    /// <summary>Read and seek only within the selected upload window.</summary>
    public static Task WindowAsync()
    {
        using var bytes=new MemoryStream(new byte[]{0,1,2,3});using var window=new global::Massdex.SyncShark.Engine.Dropbox.UploadSlice(bytes,1,2);
        Check.Require(window.ReadByte()==1 && window.ReadByte()==2 && window.ReadByte()==-1 && window.Seek(0,SeekOrigin.Begin)==0 && window.ReadByte()==1,"Upload window escaped its source range");
        return Task.CompletedTask;
    }
}
