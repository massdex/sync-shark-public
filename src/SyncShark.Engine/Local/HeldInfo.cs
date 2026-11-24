namespace Massdex.SyncShark.Engine.Local;

/// <summary>The identity, attributes, size and write time read from one held handle.</summary>
internal sealed record HeldInfo(DirectoryIdentity Identity,FileAttributes Attributes,long Size,DateTimeOffset Modified)
{
    internal string Id=>LocalIdentity.FileIdText(Identity.FileId);
    internal string Domain=>LocalSide.Domain(Identity);
    internal bool Folder=>(Attributes&FileAttributes.Directory)!=0;
    internal Entry Entry(string path,string hash)=>new(path,Folder,Folder?0:Size,Folder?default:Modified,Id,"",hash,null);
}
