// SPDX-License-Identifier: GPL-2.0-or-later
namespace Zeus.Community.Kpa1500;

public sealed record LogEntry(string Ts, string Kind, string Message);

/// <summary>Small in-memory ring of recent events for the panel's log view.</summary>
public sealed class EventLog
{
    private readonly LogEntry[] _ring;
    private int _next;
    private int _count;
    private readonly object _gate = new();

    public EventLog(int capacity) => _ring = new LogEntry[capacity];

    public void Add(string kind, string message)
    {
        var e = new LogEntry(DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), kind, message);
        lock (_gate)
        {
            _ring[_next] = e;
            _next = (_next + 1) % _ring.Length;
            if (_count < _ring.Length) _count++;
        }
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate)
        {
            var list = new List<LogEntry>(_count);
            for (var i = 1; i <= _count; i++)
                list.Add(_ring[(_next - i + _ring.Length) % _ring.Length]);
            return list;
        }
    }
}
