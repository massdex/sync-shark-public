using System.Runtime.InteropServices;
namespace Massdex.SyncShark.Engine.Local;
/// <summary>Classifies metadata without opening placeholder data.</summary>
public static class Placeholders
{
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    public static bool IsOnlineOnlyAttributes(FileAttributes attributes) =>
        (attributes & (FileAttributes.Offline | RecallOnDataAccess | RecallOnOpen)) != 0;

    public static bool IsCloudFilesReparseTag(uint tag) => (tag & 0xFFFF0FFFu) == 0x9000001Au;

    public static bool IsOnlineOnly(FileAttributes attributes, uint reparseTag) =>
        Skip(attributes, reparseTag) == TextKey.SkipOnlineOnly;

    public static bool IsLinkReparseTag(uint tag) => tag is 0xA0000003u or 0xA000000Cu or 0xA000001Du;

    internal static TextKey? Skip(FileAttributes attrs, uint tag)
    {
        bool reparse = (attrs & FileAttributes.ReparsePoint) != 0;
        if (reparse && IsLinkReparseTag(tag)) return TextKey.SkipLink;
        bool nonresident = IsOnlineOnlyAttributes(attrs);
        if (nonresident && (attrs & FileAttributes.Directory) != 0) return TextKey.SkipUnreadable;
        if (reparse && !IsCloudFilesReparseTag(tag)) return nonresident ? TextKey.SkipUnreadable : TextKey.SkipSpecial;
        return nonresident ? TextKey.SkipOnlineOnly : null;
    }
    private static readonly Lazy<bool> exposed = new(() => { try { return RtlSetProcessPlaceholderCompatibilityMode(2) >= 0 && RtlSetProcessPlaceholderCompatibilityMode(2) == 2; } catch (EntryPointNotFoundException) { return false; } });
    internal static void Expose()
    {
        if (!exposed.Value) throw new IOException(Text.Get(TextKey.RootLocalIdentityUnavailable));
    }
    [DllImport("ntdll.dll")] private static extern sbyte RtlSetProcessPlaceholderCompatibilityMode(sbyte mode);
}
