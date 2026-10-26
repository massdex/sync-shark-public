using System.Collections.Concurrent;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private sealed class GapRecoveryDiagnostics
    {
        private readonly ConcurrentQueue<string> _events = new();
        internal void Add(string text) => _events.Enqueue(text);
        internal void Attach(CountingWriter writer, Side side)
        {
            writer.OnEffect = (kind, path) => Add($"effect side={side} kind={kind} path={JsonSerializer.Serialize(path)}");
            writer.OnRead = path => Add($"observe side={side} path={JsonSerializer.Serialize(path)}");
            writer.OnContentRead = path => Add($"content-read side={side} path={JsonSerializer.Serialize(path)}");
        }
        internal void Report(RunReport report)
        {
            if (report.Failed == 0 && report.Uncertain == 0) return;
            Add($"report failed={report.Failed} uncertain={report.Uncertain} failures={JsonSerializer.Serialize(report.Failures)}");
            Dump();
        }
        internal void Dump()
        {
            var sequence = 0;
            foreach (var entry in _events.Take(100))
                Console.WriteLine($"GAP-DIAGNOSTIC {++sequence}: {entry}");
        }
    }
}
