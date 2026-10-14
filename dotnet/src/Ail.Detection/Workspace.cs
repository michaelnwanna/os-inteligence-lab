using Ail.Ingestion;

namespace Ail.Detection;

/// <summary>Lazy, cached analytic views over a telemetry store. Create a new Workspace when the data changes.</summary>
public sealed class Workspace
{
    private readonly Dictionary<string, Cube> _cubes = new();
    private readonly Dictionary<string, CohortScanner> _scanners = new();
    private readonly object _lock = new();

    public TelemetryStore Store { get; }
    public DetectionConfig Cfg { get; }
    public long T0 { get; }
    public long T1 { get; }
    public List<string> Metrics { get; }
    public List<string> SignatureMetrics { get; }

    public Workspace(TelemetryStore store, DetectionConfig? cfg = null, long? start = null)
    {
        Store = store; Cfg = cfg ?? new DetectionConfig();
        var range = store.TimeRange() ?? throw new InvalidOperationException("the store contains no telemetry");
        T0 = start is { } s ? Math.Max(range.Lo, s) : range.Lo;
        T1 = range.Hi;
        var all = store.Metrics();
        Metrics = all.Where(m => !m.StartsWith("sig:")).ToList();
        SignatureMetrics = all.Where(m => m.StartsWith("sig:")).ToList();
    }

    public int T => (int)(T1 - T0 + 1);
    public int BaselineEnd => Cfg.BaselineMinutes;

    public Cube Cube(string metric)
    {
        lock (_lock)
        {
            if (!_cubes.TryGetValue(metric, out var cube))
                _cubes[metric] = cube = Detection.Cube.FromRows(metric, Store.AggRows(metric), T0, T1);
            return cube;
        }
    }

    public CohortScanner Scanner(string metric)
    {
        lock (_lock)
        {
            if (!_scanners.TryGetValue(metric, out var sc))
                _scanners[metric] = sc = new CohortScanner(Cube(metric), BaselineEnd, Cfg.Cohort);
            return sc;
        }
    }
}
