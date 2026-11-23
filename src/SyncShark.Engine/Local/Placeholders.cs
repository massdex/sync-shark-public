using System.Runtime.InteropServices;
namespace Massdex.SyncShark.Engine.Local;
/// <summary>Classifies metadata without opening placeholder data.</summary>
public static class Placeholders
{
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    [ThreadStatic] private static int? residencyCalls;
    public static bool IsOnlineOnlyAttributes(FileAttributes attributes)
    {
        if (residencyCalls is int count)
        {
            if (count != 0) throw new InvalidOperationException("Ordinary file residency evaluated twice in one classification");
            residencyCalls = count + 1;
        }
        return (attributes & (FileAttributes.Offline | RecallOnDataAccess | RecallOnOpen)) != 0;
    }

    public static bool IsCloudFilesReparseTag(uint tag) => (tag & 0xFFFF0FFFu) == 0x9000001Au;

    public static bool IsLocalCloudFilesObject(FileAttributes attributes, uint reparseTag) =>
        (attributes & FileAttributes.ReparsePoint) != 0 && IsCloudFilesReparseTag(reparseTag) &&
        !IsOnlineOnlyAttributes(attributes);

    public static bool IsOnlineOnly(FileAttributes attributes, uint reparseTag) =>
        (attributes & FileAttributes.Directory) == 0 && IsOnlineOnlyAttributes(attributes) &&
        ((attributes & FileAttributes.ReparsePoint) == 0 || IsCloudFilesReparseTag(reparseTag));

    public static bool IsLinkReparseTag(uint tag) => tag is 0xA0000003u or 0xA000000Cu or 0xA000001Du;

    public static bool IsLink(FileAttributes attributes, uint reparseTag) =>
        (attributes & FileAttributes.ReparsePoint) != 0 && IsLinkReparseTag(reparseTag);

    internal static TextKey? Skip(FileAttributes attrs,uint tag)
    {
        residencyCalls = (attrs & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0 ? 0 : null;
        try { return IsLink(attrs,tag) ? TextKey.SkipLink :
        IsOnlineOnly(attrs,tag) ? TextKey.SkipOnlineOnly :
        IsOnlineOnlyAttributes(attrs) ? TextKey.SkipUnreadable :
        (attrs.HasFlag(FileAttributes.ReparsePoint) && !IsLocalCloudFilesObject(attrs,tag)) ? TextKey.SkipSpecial : null; }
        finally { residencyCalls = null; }
    }
    private static readonly Lazy<bool> exposed = new(() => { try { return RtlSetProcessPlaceholderCompatibilityMode(2) >= 0 && RtlSetProcessPlaceholderCompatibilityMode(2) == 2; } catch (EntryPointNotFoundException) { return false; } });
    internal static void Expose()
    {
        if (!exposed.Value) throw new IOException(Text.Get(TextKey.RootLocalIdentityUnavailable));
    }
    [DllImport("ntdll.dll")] private static extern sbyte RtlSetProcessPlaceholderCompatibilityMode(sbyte mode);
}
