using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>Matches supplied personal-data patterns against paths and decoded file bytes.</summary>
public sealed class PersonalLiteralScan
{
    private readonly Regex[] patterns;

    public PersonalLiteralScan(string[] patterns)
    {
        this.patterns = Array.ConvertAll(patterns ?? Array.Empty<string>(),
            pattern => new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled));
    }

    public static string[] Decode(byte[] bytes) => new[] {
        Encoding.UTF8.GetString(bytes), Encoding.Unicode.GetString(bytes), Encoding.BigEndianUnicode.GetString(bytes)
    };

    /// <summary>Includes the relative path even when the file contains no text.</summary>
    public bool Matches(string relative, string text)
    {
        var candidate = relative + "\n" + text;
        foreach (var pattern in patterns)
            if (pattern.IsMatch(candidate)) return true;
        return false;
    }

    /// <summary>Standalone checks use the same decoding and matcher as an in-memory export.</summary>
    public static string[] Scan(string root, FileInfo[] files, string[] patterns)
    {
        var matcher = new PersonalLiteralScan(patterns);
        var findings = new List<string>();
        foreach (var file in files)
        {
            var relative = file.FullName.Substring(root.Length);
            if (matcher.Matches(relative, string.Join("\n", Decode(File.ReadAllBytes(file.FullName)))))
                findings.Add(relative);
        }
        return findings.ToArray();
    }
}
