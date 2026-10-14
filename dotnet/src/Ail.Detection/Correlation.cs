using Ail.Schemas;

namespace Ail.Detection;

public static class Labels
{
    public static readonly Dictionary<string, string> MetricLabels = new()
    {
        ["failure_rate"] = "Failure rate", ["latency_ms"] = "Latency", ["memory_mb"] = "Memory usage",
    };

    public static string MetricLabel(string metric) => MetricLabels.GetValueOrDefault(metric, metric);

    public static string FormatValue(string metric, double value) => metric switch
    {
        "failure_rate" => $"{value * 100:F2}%",
        "latency_ms" => $"{value:F0} ms",
        "memory_mb" => $"{value:F0} MB",
        _ => $"{value:F0}",
    };

    /// <summary>e.g. {architecture: arm64, application_version: 2.1} becomes "ARM64 + App 2.1".</summary>
    public static string CohortLabel(IReadOnlyDictionary<string, string>? dims)
    {
        if (dims is null || dims.Count == 0) return "all devices";
        var parts = new List<string>();
        foreach (var key in Dimensions.All)
        {
            if (!dims.TryGetValue(key, out var v)) continue;
            parts.Add(key switch
            {
                "architecture" => v.ToUpperInvariant(),
                "application_version" => $"App {v}",
                "driver_version" => $"Driver {v}",
                "os_version" => $"OS {v}",
                _ => v,
            });
        }
        return string.Join(" + ", parts);
    }
}

/// <summary>Change correlation: rank nearby changes as candidate triggers (evidence, not proof of causation).</summary>
public static class Correlation
{
    public static List<CorrelationCandidate> Correlate(Cube cube, CohortScanner scanner, bool[]? cohortMask, int onsetIdx, int endIdx,
        IReadOnlyList<ChangeEvent> changes, CorrelationConfig cfg, bool[]? ignore = null)
    {
        var (nw, kw, sw, _) = cube.Window(onsetIdx, endIdx + 1);
        var excess = new double[cube.C];
        for (var c = 0; c < cube.C; c++)
            excess[c] = ignore is not null && ignore[c] ? 0.0 : (cube.Kind == "rate" ? kw[c] : sw[c]) - nw[c] * scanner.Level[c];
        var inCohort = cohortMask ?? Enumerable.Repeat(true, cube.C).ToArray();
        var onsetBucket = cube.T0 + onsetIdx;
        var output = new List<CorrelationCandidate>();
        foreach (var ch in changes)
        {
            var delta = (double)(onsetBucket - TimeUtil.BucketOf(ch.Timestamp));
            if (delta > cfg.LookbackMinutes) continue;
            var touched = ch.Dimension is not null && ch.ToVersion is not null
                ? cube.Mask(new Dictionary<string, string> { [ch.Dimension] = ch.ToVersion })
                : Enumerable.Repeat(true, cube.C).ToArray();
            double inter = 0, union = 0;
            for (var c = 0; c < cube.C; c++)
            {
                if (inCohort[c] && touched[c]) inter += excess[c];
                if (inCohort[c] || touched[c]) union += excess[c];
            }
            var overlap = union > 0 && inter > 0 ? Math.Clamp(inter / union, 0.0, 1.0) : 0.0;
            var after = delta < -cfg.OnsetToleranceMinutes;
            var temporal = after ? 0.0 : Math.Exp(-Math.Max(delta, 0.0) / cfg.TauMinutes);
            var score = Math.Round(cfg.WeightTemporal * temporal + cfg.WeightOverlap * overlap, 4);
            var when = TimeUtil.Hhmm(TimeUtil.BucketOf(ch.Timestamp));
            var timing = after
                ? $"began {when}, {Math.Abs(delta):F0} min after the anomaly onset, so it cannot be its trigger"
                : $"began {when}, {Math.Max(delta, 0):F0} min before the anomaly onset";
            output.Add(new CorrelationCandidate
            {
                Change = ch, Score = score, TemporalScore = Math.Round(temporal, 4), Overlap = Math.Round(overlap, 4), DeltaMinutes = delta,
                AfterOnset = after,
                Rationale = $"{ch.Component} {ch.FromVersion}->{ch.ToVersion} ({ch.Kind}) {timing}; {overlap * 100:F0}% overlap with the affected population.",
            });
        }
        return output.OrderByDescending(c => c.Score).ToList();
    }
}
