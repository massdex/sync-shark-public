using System.ComponentModel;
using System.Collections.Concurrent;

namespace Massdex.SyncShark.V2;

/// <summary>Fake exceptions and in-memory log sinks only; no filesystem, Defender or elevation calls.</summary>
internal static class NativeIoDiagnosticChecks
{
    internal static void Run()
    {
        var unknown = NativeIoDiagnostic.Describe("fixture.txt", new IOException("generic failure"));
        Check(unknown.Contains("Win32=unknown") && unknown.Contains("HRESULT=0x80131620"),
            "generic IOException must not invent a Win32 code from its low word");

        foreach (var code in new[] { 5, 225, 226 })
        {
            var detail = NativeIoDiagnostic.Describe(@"C:\fake\item.txt",
                new IOException("wrapped", new Win32Exception(code)));
            Check(detail.Contains($"Win32={code},") && detail.Contains($"HRESULT=0x{(0x80070000u | (uint)code):X8}"),
                "nested native errors retain their exact code and mapped HRESULT");
        }
        var mapped = NativeIoDiagnostic.Describe("fixture.txt", new IOException("removed", unchecked((int)0x800700E2u)));
        Check(mapped.Contains("Win32=226,") && mapped.Contains("HRESULT=0x800700E2"),
            "HRESULT_FROM_WIN32 exceptions decode without a Win32Exception wrapper");

        var path = @"C:\fake\" + new string('x', 4096) + "\r\nitem.txt";
        var failure = new IOException("line one\r\nline two", new Win32Exception(225));
        var lines = new List<string>();
        NativeIoDiagnostic.LogOnce(path, failure, lines.Add);
        var wrapped = new IOException("caller wrapper", failure);
        NativeIoDiagnostic.LogOnce("pair-relative.txt", wrapped, lines.Add);
        Check(lines.Count == 1, "a folder failure wrapped by the transfer layer logs once");
        Check(lines[0].Contains(path.Replace("\r", "\\r").Replace("\n", "\\n")) &&
            !lines[0].Contains('\r') && !lines[0].Contains('\n'), "exact long paths are not truncated and logs stay one line");
        Check(NativeIoDiagnostic.Describe("pair-relative.txt", wrapped).Contains(@"C:\fake\"),
            "outer callers retain the deepest failure's exact path");
        NativeIoDiagnostic.LogOnce(path, new IOException("new attempt", new Win32Exception(225)), lines.Add);
        Check(lines.Count == 2, "a later independent failure is not suppressed");

        var concurrent = new ConcurrentQueue<string>();
        var shared = new IOException("shared failure", new Win32Exception(226));
        Parallel.For(0, 16, _ => NativeIoDiagnostic.LogOnce("fake.txt", shared, concurrent.Enqueue));
        Check(concurrent.Count == 1, "concurrent wrappers do not duplicate the same diagnostic");
        Console.WriteLine("PASS native I/O diagnostics: native codes, unknown HRESULT, exact path, one-line and deduplication (fakes only)");
    }

    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException("Native I/O diagnostic check failed: " + reason);
    }
}
