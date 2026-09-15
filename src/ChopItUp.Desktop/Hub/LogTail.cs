namespace ChopItUp.Desktop.Hub;

/// <summary>Row 12 T3: a bounded ring buffer of the last <paramref name="capacity"/> lines a hub
/// child printed, shown on the failure boot page. Thread-safe: output lines arrive on the process's
/// own read-loop thread while the UI thread can snapshot it at any time.</summary>
public sealed class LogTail(int capacity)
{
    private readonly object _gate = new();
    private readonly Queue<string> _lines = new();

    public void Add(string line)
    {
        lock (_gate)
        {
            _lines.Enqueue(line);
            while (_lines.Count > capacity)
                _lines.Dequeue();
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
            return _lines.ToArray();
    }
}
