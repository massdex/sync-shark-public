using System.Runtime.InteropServices;
namespace Massdex.SyncShark.Engine.Local;
/// <summary>Classifies metadata without opening placeholder data.</summary>
public static class Placeholders
{
    // winnt.h FILE_ATTRIBUTE_RECALL_ON_OPEN and FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS.
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    public static bool IsOnlineOnlyAttributes(FileAttributes attributes) =>
        (attributes & (FileAttributes.Offline | RecallOnDataAccess | RecallOnOpen)) != 0;

    // winnt.h IO_REPARSE_TAG_*; the cloud mask selects the variant bits.
    private const uint IO_REPARSE_TAG_CLOUD_MASK=0x0000F000, IO_REPARSE_TAG_CLOUD=0x9000001A;
    private const uint IO_REPARSE_TAG_MOUNT_POINT=0xA0000003, IO_REPARSE_TAG_SYMLINK=0xA000000C, IO_REPARSE_TAG_LX_SYMLINK=0xA000001D;
    public static bool IsCloudFilesReparseTag(uint tag) => (tag & ~IO_REPARSE_TAG_CLOUD_MASK) == IO_REPARSE_TAG_CLOUD;

    public static bool IsOnlineOnly(FileAttributes attributes, uint reparseTag) =>
        Skip(attributes, reparseTag) == TextKey.SkipOnlineOnly;

    public static bool IsLinkReparseTag(uint tag) => tag is IO_REPARSE_TAG_MOUNT_POINT or IO_REPARSE_TAG_SYMLINK or IO_REPARSE_TAG_LX_SYMLINK;

    internal static TextKey? Skip(FileAttributes attrs, uint tag)
    {
        bool reparse = (attrs & FileAttributes.ReparsePoint) != 0;
        if (reparse && IsLinkReparseTag(tag)) return TextKey.SkipLink;
        bool nonresident = IsOnlineOnlyAttributes(attrs);
        if (nonresident && (attrs & FileAttributes.Directory) != 0) return TextKey.SkipUnreadable;
        if (reparse && !IsCloudFilesReparseTag(tag)) return nonresident ? TextKey.SkipUnreadable : TextKey.SkipSpecial;
        return nonresident ? TextKey.SkipOnlineOnly : null;
    }
    private const sbyte ExposePlaceholders=2; // ntifs.h PHCM_EXPOSE_PLACEHOLDERS
    private static readonly Lazy<bool> exposed = new(() => { try { return RtlSetProcessPlaceholderCompatibilityMode(ExposePlaceholders) >= 0 && RtlSetProcessPlaceholderCompatibilityMode(ExposePlaceholders) == ExposePlaceholders; } catch (EntryPointNotFoundException) { return false; } });
    internal static void Expose()
    {
        if (!exposed.Value) throw new IOException(Text.Get(TextKey.RootLocalIdentityUnavailable));
    }
    [DllImport("ntdll.dll")] private static extern sbyte RtlSetProcessPlaceholderCompatibilityMode(sbyte mode);
}
