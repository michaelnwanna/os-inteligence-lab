using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace Ail.Ingestion;

/// <summary>Self-observability: dependency-free counters and summaries with Prometheus text output.</summary>
public sealed class MetricsRegistry
{
    public static readonly MetricsRegistry Instance = new();

    private readonly ConcurrentDictionary<string, double> _counters = new();
    private readonly ConcurrentDictionary<string, (double Count, double Sum, double Max)> _summaries = new();
    private readonly ConcurrentDictionary<string, Func<double>> _gauges = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<(long Ticks, double Value)>> _marks = new();

    public void Inc(string name, double value = 1)
    {
        _counters.AddOrUpdate(name, value, (_, old) => old + value);
        var q = _marks.GetOrAdd(name, _ => new());
        q.Enqueue((Stopwatch.GetTimestamp(), value));
        while (q.TryPeek(out var head) && Stopwatch.GetElapsedTime(head.Ticks).TotalSeconds > 60) q.TryDequeue(out _);
    }

    public void Observe(string name, double value) =>
        _summaries.AddOrUpdate(name, (1, value, value), (_, s) => (s.Count + 1, s.Sum + value, Math.Max(s.Max, value)));

    public void Gauge(string name, Func<double> fn) => _gauges[name] = fn;

    public double Rate(string name, double windowSeconds = 10)
    {
        if (!_marks.TryGetValue(name, out var q)) return 0;
        return q.Where(m => Stopwatch.GetElapsedTime(m.Ticks).TotalSeconds <= windowSeconds).Sum(m => m.Value) / windowSeconds;
    }

    public T Time<T>(string name, Func<T> work)
    {
        var start = Stopwatch.GetTimestamp();
        try { return work(); }
        finally { Observe(name, Stopwatch.GetElapsedTime(start).TotalSeconds); }
    }

    public Dictionary<string, double> Snapshot()
    {
        var o = new Dictionary<string, double>(_counters);
        foreach (var (k, s) in _summaries) { o[k + "_count"] = s.Count; o[k + "_sum"] = s.Sum; o[k + "_max"] = s.Max; }
        foreach (var (k, fn) in _gauges) o[k] = fn();
        return o;
    }

    public string Render()
    {
        var lines = Snapshot().OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value.ToString("R", CultureInfo.InvariantCulture)}").ToList();
        lines.Add($"ail_ingest_events_per_second {Rate("ail_ingest_events_total").ToString("R", CultureInfo.InvariantCulture)}");
        return string.Join("\n", lines) + "\n";
    }
}
