using System.Text;

namespace Massdex.SyncShark.Engine.Local;

/// <summary>The user's settings file at &lt;productRoot&gt;/engine/settings.json (section 2.2).</summary>
public static class SettingsFile
{
    /// <summary>Null when the file is absent.</summary>
    public static string? Read(string productRoot)
    {
        try
        {
            return File.ReadAllText(Path.Combine(productRoot, ResourceNames.EngineFolder, ResourceNames.UserSettingsFile));
        }
        catch (IOException error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Writes the user's JSON, creating the engine folder when needed. The caller passes the
    /// product's atomic-replace policy so a brief external hold of the settings file is retried (#135).</summary>
    public static void Write(string productRoot, string json, AtomicReplacePolicy policy) =>
        Write(productRoot, json, policy, null);

    /// <param name="onDelay">Test seam: runs before each retry instead of waiting out the policy's delay.</param>
    internal static void Write(string productRoot, string json, AtomicReplacePolicy policy, Action<TimeSpan>? onDelay)
    {
        var directory = Path.Combine(productRoot, ResourceNames.EngineFolder);
        Directory.CreateDirectory(directory);
        // Publish by durable flush then same-directory rename: an interruption leaves the old complete
        // JSON or the new one, never a truncated prefix. Encoding.UTF8.GetBytes emits no BOM.
        var replacement = Path.Combine(directory, ResourceNames.UserSettingsReplacementFile);
        using (var stream = new FileStream(replacement, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(Encoding.UTF8.GetBytes(json));
            stream.Flush(flushToDisk: true);
        }
        AtomicFile.Move(replacement, Path.Combine(directory, ResourceNames.UserSettingsFile), overwrite: true, policy,
            default, onDelay);
    }
}
