namespace Massdex.SyncShark;

/// <summary>Fixed Dropbox provider facts shared by the Dropbox CLI verbs (design dropbox-cli.md §4) and the
/// guarded transport (READING 8.1/8.4).</summary>
public static class DropboxApiLimits
{
    /// <summary>Dropbox accepts at most this many entries in one delete/move/copy batch.</summary>
    public const int BatchMaxEntries = 1000;

    /// <summary>The smallest long-poll wait, in seconds, that Dropbox accepts.</summary>
    public const int LongpollMinSeconds = 30;

    /// <summary>The largest long-poll wait, in seconds, that Dropbox accepts.</summary>
    public const int LongpollMaxSeconds = 480;

    /// <summary>The largest file Dropbox accepts in a single files/upload request, in bytes.</summary>
    public const long SingleUploadMaxBytes = 150L * 1024 * 1024;

    /// <summary>An upload-session chunk size must be a multiple of this many bytes.</summary>
    public const int UploadChunkMultiple = 4 * 1024 * 1024;

    /// <summary>The most bytes read from a Dropbox error body.</summary>
    public const int ErrorBodyMaxBytes = 64 * 1024;

    /// <summary>Dropbox's connect allowance for an ordinary request (known fact KF-2026-10-31-2 D14).</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

    /// <summary>The progress allowance of an ordinary read and of each upload write slice
    /// (known fact KF-2026-10-31-2 D15; READING 8.4).</summary>
    public static readonly TimeSpan ReadWriteAllowance = TimeSpan.FromSeconds(120);

    /// <summary>A long-poll read is allowed its requested timeout plus this allowance
    /// (known fact KF-2026-10-31-2 D16).</summary>
    public static readonly TimeSpan LongpollTimeoutAllowance = TimeSpan.FromSeconds(90);
}
