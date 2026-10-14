using Ail.Ingestion;
using Ail.Schemas;

namespace Ail.Detection;

/// <summary>
/// End-to-end detection: detectors, cohort scan, incident grouping, correlation. Detection runs in passes: when every
/// incident found in a pass has recovered, the engine re-baselines on the data after the recovery and searches again.
/// </summary>
public static class IncidentAnalyzer
{
    public const string EngineVersion = "0.2.0-dotnet";
    private const int MaxPasses = 6;
    private static readonly string[] MetricPriority = { "failure_rate", "memory_mb", "latency_ms" };

    private sealed class Cluster
    {
        public required string Metric { get; init; }
        public required List<AnomalyResult> Results { get; init; }
        public int Onset { get; init; }
        public int Detected { get; init; }
    }

    private static int Idx(AnomalyResult r, string key) => Convert.ToInt32(r.Evidence[key]);
    private static bool IsCohortScan(AnomalyResult r) => r.Detector == "CohortScanDetector";

    private static AnomalyResult CohortResult(Workspace ws, string metric, int alarm, CohortFinding finding, bool[] mask)
    {
        var cfg = ws.Cfg;
        var scanner = ws.Scanner(metric);
        var end = Math.Min(alarm + cfg.ConfirmMinutes, ws.T - 1);
        var onset = scanner.Onset(mask, end);
        var ev = scanner.Evaluate(mask, onset, end + 1);
        var ratio = ev.Baseline > 0 ? ev.Observed / ev.Baseline : finding.Ratio;
        var (severity, score) = Stats.SeverityFor(ratio, ws.Cube(metric).Kind);
        var strength = Math.Min(5.0, Math.Log10(1 / Math.Max(finding.PValue, 1e-300)) / Math.Log10(1 / cfg.Cohort.Alpha));
        return new AnomalyResult
        {
            Metric = metric, Timestamp = TimeUtil.DtOf(ws.T0 + alarm), Onset = TimeUtil.DtOf(ws.T0 + onset),
            Baseline = ev.Baseline, ObservedValue = ev.Observed, Delta = ev.Observed - ev.Baseline, Severity = severity,
            SeverityScore = Math.Round(score, 4), Confidence = Stats.ConfidenceFromStrength(strength),
            AffectedPopulation = finding.PopulationShare, Detector = "CohortScanDetector", Dimensions = finding.Dimensions,
            Evidence = new()
            {
                ["alarm_minute_index"] = alarm, ["onset_minute_index"] = onset, ["llr"] = Math.Round(finding.Llr, 2),
                ["p_value"] = finding.PValue, ["ratio"] = Math.Round(ratio, 3), ["strength"] = Math.Round(strength, 3),
                ["window_minutes"] = cfg.Cohort.WindowMinutes, ["confirmed"] = true,
            },
        };
    }

    private static bool DimsCompatible(IReadOnlyDictionary<string, string>? a, IReadOnlyDictionary<string, string>? b)
    {
        if (a is null || b is null || a.Count == 0 || b.Count == 0) return true;
        var (small, big) = a.Count <= b.Count ? (a, b) : (b, a);
        return small.All(kv => big.TryGetValue(kv.Key, out var v) && v == kv.Value);
    }

    private static List<List<Cluster>> Group(List<AnomalyResult> results, DetectionConfig cfg)
    {
        var clusters = new List<Cluster>();
        foreach (var byMetric in results.GroupBy(r => r.Metric))
        {
            var rs = byMetric.ToList();
            var first = rs.Min(r => Idx(r, "alarm_minute_index"));
            var members = rs.Where(r => Idx(r, "alarm_minute_index") <= first + cfg.MergeMinutes).ToList();
            var cohortOnsets = members.Where(IsCohortScan).Select(r => Idx(r, "onset_minute_index")).ToList();
            int onset;
            if (cohortOnsets.Count > 0) onset = cohortOnsets.Min();
            else
            {
                var sorted = members.Select(r => Idx(r, "onset_minute_index")).OrderBy(x => x).ToList();
                onset = sorted[(sorted.Count - 1) / 2]; // median_low
            }
            clusters.Add(new Cluster { Metric = byMetric.Key, Results = members, Onset = onset, Detected = first });
        }
        clusters = clusters.OrderBy(c => c.Detected).ThenBy(c => c.Metric, StringComparer.Ordinal).ToList();

        Dictionary<string, string>? CohortDims(IEnumerable<Cluster> group)
        {
            var dims = group.SelectMany(c => c.Results).Where(IsCohortScan).Select(r => r.Dimensions).ToList();
            return dims.Count == 0 ? null : dims.MaxBy(d => d.Count);
        }

        var groups = new List<List<Cluster>>();
        foreach (var c in clusters)
        {
            var mine = CohortDims(new[] { c });
            var home = groups.FirstOrDefault(g => Math.Abs(g[0].Onset - c.Onset) <= cfg.IncidentOnsetMinutes && DimsCompatible(CohortDims(g), mine));
            if (home is null) groups.Add(new() { c }); else home.Add(c);
        }
        return groups;
    }

    private static List<Dictionary<string, object?>> SignatureSummary(Workspace ws, Dictionary<string, string> dims, int onset, int last)
    {
        var fCube = ws.Cube("failure_rate"); var fScan = ws.Scanner("failure_rate");
        var fMask = fCube.Mask(dims);
        var (nw, kw, _, _) = fCube.Window(onset, last + 1);
        double expected = 0, total = 0;
        for (var c = 0; c < fCube.C; c++) if (fMask[c]) { expected += nw[c] * fScan.Level[c]; total += kw[c]; }
        var baseCounts = new Dictionary<string, double>(); var winCounts = new Dictionary<string, double>();
        foreach (var m in ws.SignatureMetrics)
        {
            Cube cube;
            try { cube = ws.Cube(m); }
            catch (InvalidOperationException) { continue; } // this signature never occurs inside the analysis window
            baseCounts[m] = cube.Window(0, ws.BaselineEnd).N.Sum();
            var mask = cube.Mask(dims);
            var win = cube.Window(onset, last + 1).N;
            winCounts[m] = Enumerable.Range(0, cube.C).Where(c => mask[c]).Sum(c => win[c]);
        }
        var baseTotal = Math.Max(baseCounts.Values.Sum(), 1.0);
        var rows = winCounts.Select(kv =>
        {
            var shareB = baseCounts[kv.Key] / baseTotal;
            return new { Sig = kv.Key.Substring(4), Count = kv.Value, ShareB = shareB, Excess = Math.Max(kv.Value - expected * shareB, 0.0) };
        }).ToList();
        var totalExcess = rows.Sum(r => r.Excess);
        if (totalExcess == 0) totalExcess = 1;
        return rows.Where(r => r.Count > 0).OrderByDescending(r => r.Excess).Take(5).Select(r => new Dictionary<string, object?>
        {
            ["signature"] = r.Sig, ["count"] = r.Count, ["baseline_share"] = r.ShareB,
            ["share_of_failures"] = total > 0 ? r.Count / total : 0.0, ["excess"] = r.Excess, ["excess_share"] = r.Excess / totalExcess,
        }).ToList();
    }

    /// <summary>First minute of a clean `RecoveryMinutes` window for the scope, or null if it never recovers.</summary>
    private static int? RecoveryIndex(CohortScanner scanner, bool[] mask, int start, int last, DetectionConfig cfg)
    {
        var w = cfg.RecoveryMinutes;
        for (var end = start + w - 1; end <= last; end++)
            if (scanner.Evaluate(mask, end - w + 1, end + 1).Llr < cfg.RecoveryZ * cfg.RecoveryZ / 2) return end - w + 1;
        return null;
    }

    private static (Incident Incident, List<(CohortFinding Finding, bool[] Mask)> Findings)? BuildIncident(
        Workspace ws, List<Cluster> group, IReadOnlyList<ChangeEvent> changes, int pick = 0, List<(CohortFinding Finding, bool[] Mask)>? seeded = null)
    {
        var cfg = ws.Cfg;
        var present = group.ToDictionary(c => c.Metric);
        var primary = MetricPriority.Where(present.ContainsKey).Select(m => present[m]).FirstOrDefault() ?? group[0];
        var metric = primary.Metric;
        var cube = ws.Cube(metric); var scanner = ws.Scanner(metric);
        var last = ws.T - 1;
        var detected = group.Min(c => c.Detected);
        int? ownAlarm = null;
        var onset = Math.Min(detected, group.Min(c => c.Onset));
        var allResults = group.SelectMany(c => c.Results).OrderBy(r => r.Timestamp).ToList();

        // The cohort that raised the alarm defines "recovered"; scanning all remaining history would dilute it.
        var alarmDims = primary.Results.Where(IsCohortScan).Select(r => r.Dimensions).FirstOrDefault();
        bool[] seedMask;
        if (pick == 0)
        {
            seedMask = alarmDims is { Count: > 0 } ? cube.Mask(alarmDims) : Enumerable.Repeat(true, cube.C).ToArray();
        }
        else
        {
            seedMask = seeded![pick].Mask;
            onset = Math.Min(detected, scanner.Onset(seedMask, last));
        }

        var recovered = RecoveryIndex(scanner, seedMask, detected, last, cfg);
        var end = recovered is { } rec ? rec - 1 : last;
        var findings = scanner.Scan(onset, end + 1);
        var idx = 0;
        if (pick > 0)
        {
            var overlap = findings.Select(f => (double)f.Mask.Zip(seedMask, (a, b) => a && b).Count(x => x) / Math.Max(f.Mask.Zip(seedMask, (a, b) => a || b).Count(x => x), 1)).ToList();
            if (overlap.Count == 0 || overlap.Max() <= 0) return null;
            idx = overlap.IndexOf(overlap.Max());
        }
        var mask = findings.Count > 0 ? findings[idx].Mask : null;
        var others = new bool[cube.C];
        for (var i = 0; i < findings.Count; i++)
            if (i != idx) for (var c = 0; c < cube.C; c++) others[c] |= findings[i].Mask[c];
        var cohorts = findings.Count > 0 ? new[] { findings[idx].Finding }.Concat(findings.Where((_, i) => i != idx).Select(f => f.Finding)).ToList() : new List<CohortFinding>();
        var focus = cohorts.FirstOrDefault();
        var dims = focus?.Dimensions ?? new Dictionary<string, string>();
        // The shared alarm may have come from another cohort; detection is when this cohort alarms with the others removed.
        if (focus is not null && others.Any(x => x) && alarmDims is { Count: > 0 } && !DimsCompatible(alarmDims, dims)
            && scanner.FirstAlarm(null, others) is { } own && DimsCompatible(own.Finding.Dimensions, dims) && own.Index > detected)
        {
            detected = own.Index;
            ownAlarm = own.Index;
        }
        if (focus is not null) onset = Math.Min(detected, scanner.Onset(mask!, end)); // the cohort's own change point is sharper

        var results = allResults.Where(r => !IsCohortScan(r) || r.Dimensions.OrderBy(k => k.Key).SequenceEqual(dims.OrderBy(k => k.Key))).ToList();
        var confirmCandidates = results.Where(r => r.Evidence.TryGetValue("confirmed", out var v) && v is true)
            .Select(r => Idx(r, "alarm_minute_index") + cfg.ConfirmMinutes).ToList();
        int? confirmed = ownAlarm is { } sa ? sa + cfg.ConfirmMinutes : confirmCandidates.Count > 0 ? confirmCandidates.Min() : null;

        var series = cube.Series();
        var baseline = Stats.FitBaseline(series, ws.BaselineEnd);
        var gObs = series.Value(onset, end + 1);
        var (baseVal, obsVal) = focus is not null ? (focus.Baseline, focus.Observed) : (baseline.Level, gObs);

        var supporting = new List<Dictionary<string, object?>>();
        foreach (var m2 in ws.Metrics)
        {
            if (m2 == metric || !cfg.Metrics.Contains(m2)) continue;
            var c2 = ws.Cube(m2);
            var mask2 = dims.Count > 0 ? c2.Mask(dims) : Enumerable.Repeat(true, c2.C).ToArray();
            var ev2 = ws.Scanner(m2).Evaluate(mask2, onset, end + 1);
            supporting.Add(new() { ["metric"] = m2, ["scope"] = Labels.CohortLabel(dims), ["baseline"] = ev2.Baseline, ["observed"] = ev2.Observed, ["ratio"] = ev2.Ratio, ["significant"] = ev2.Llr > 12.0 });
        }

        var sevGlobal = Stats.SeverityFor(baseline.Level != 0 ? gObs / baseline.Level : 1.0, cube.Kind).Severity;
        var sevs = new List<string> { sevGlobal };
        sevs.AddRange(primary.Results.Where(r => !IsCohortScan(r) || r.Dimensions.OrderBy(k => k.Key).SequenceEqual(dims.OrderBy(k => k.Key))).Select(r => r.Severity));
        if (focus is not null) sevs.Add(focus.Severity);
        var severity = Severity.Max(sevs);
        var scope = Labels.CohortLabel(dims);
        var title = dims.Count > 0 ? $"{Labels.MetricLabel(metric)} regression in {scope}" : $"Global {Labels.MetricLabel(metric).ToLowerInvariant()} regression";
        var correlations = Correlation.Correlate(cube, scanner, focus is not null ? mask : null, onset, end, changes, cfg.Correlation, others.Any(x => x) ? others : null);

        var incident = new Incident
        {
            Id = "AIL-0", Title = title, Status = recovered is null ? "open" : "resolved", Severity = severity, Metric = metric,
            Onset = TimeUtil.DtOf(ws.T0 + onset), FirstDetection = TimeUtil.DtOf(ws.T0 + detected),
            Confirmed = confirmed is { } cf ? TimeUtil.DtOf(ws.T0 + cf) : null,
            ResolvedAt = recovered is { } r0 ? TimeUtil.DtOf(ws.T0 + r0) : null,
            Baseline = baseVal, Observed = obsVal, GlobalBaseline = baseline.Level, GlobalObserved = gObs,
            SupportingMetrics = supporting, Results = results, Cohorts = cohorts,
            Signatures = SignatureSummary(ws, dims, onset, end), Correlations = correlations,
            Reproducibility = new()
            {
                ["engine_version"] = EngineVersion, ["config_hash"] = cfg.Fingerprint(), ["config"] = cfg,
                ["cohort_alpha"] = cfg.Cohort.Alpha, ["data_fingerprint"] = ws.Store.DataFingerprint(),
                ["analysis_start"] = TimeUtil.DtOf(ws.T0).ToString("O"),
                ["scan_window"] = new[] { TimeUtil.DtOf(ws.T0 + onset).ToString("O"), TimeUtil.DtOf(ws.T0 + end).ToString("O") },
            },
        };
        return (incident, findings);
    }

    /// <summary>Statistical significance is not enough: require a practically meaningful effect.</summary>
    private static bool IsMaterial(Incident inc, DetectionConfig cfg)
    {
        var minCohort = inc.Metric == "failure_rate" ? cfg.Cohort.MinRatioRate : cfg.Cohort.MinRatioGauge;
        var cohortOk = inc.Cohorts.Count > 0 && inc.Cohorts[0].Ratio >= minCohort;
        var globalOk = inc.GlobalBaseline > 0 && inc.GlobalObserved / inc.GlobalBaseline >= 1 + cfg.MinRelativeChange;
        return cohortOk || globalOk;
    }

    private static List<Incident> AnalyzeWindow(TelemetryStore store, Workspace ws, IReadOnlyList<Detector> detectors)
    {
        var cfg = ws.Cfg;
        var metrics = cfg.Metrics.Where(ws.Metrics.Contains).ToList();
        if (!metrics.Contains("failure_rate") || ws.T <= ws.BaselineEnd + cfg.WindowMinutes) return new();
        var series = metrics.ToDictionary(m => m, m => ws.Cube(m).Series());
        var bases = metrics.ToDictionary(m => m, m => Stats.FitBaseline(series[m], ws.BaselineEnd));
        var ctx = new DetectionContext { Series = series, Baselines = bases, Cfg = cfg };
        var results = new List<AnomalyResult>();
        foreach (var det in detectors)
        {
            try { results.AddRange(MetricsRegistry.Instance.Time("ail_detector_latency_seconds", () => det.Detect(ctx))); }
            catch (Exception) { MetricsRegistry.Instance.Inc("ail_detector_errors_total"); }
        }
        foreach (var m in metrics)
            if (ws.Scanner(m).FirstAlarm() is { } hit) results.Add(CohortResult(ws, m, hit.Index, hit.Finding, hit.Mask));

        var changes = store.ListChanges();
        var built = new List<Incident>();
        foreach (var group in Group(results, cfg))
        {
            if (BuildIncident(ws, group, changes) is not { } first) continue;
            built.Add(first.Incident);
            var accepted = first.Incident.Cohorts.Take(1).Select(c => c.Dimensions).ToList();
            for (var k = 0; k < first.Findings.Count; k++)
            {
                var c = first.Findings[k].Finding;
                if (accepted.Any(a => a.OrderBy(x => x.Key).SequenceEqual(c.Dimensions.OrderBy(x => x.Key)))) continue;
                // a second, unrelated cohort with a large share of the excess is its own incident
                if (c.ExcessShare >= 0.2 && accepted.All(a => !DimsCompatible(a, c.Dimensions)) &&
                    BuildIncident(ws, group, changes, k, first.Findings) is { } sibling)
                {
                    built.Add(sibling.Incident);
                    accepted.Add(c.Dimensions);
                }
            }
        }
        return built.Where(i => IsMaterial(i, cfg)).ToList();
    }

    /// <summary>Run detection over everything in the store and return incidents (not yet investigated).</summary>
    public static List<Incident> Analyze(TelemetryStore store, DetectionConfig? cfg = null, IReadOnlyList<Detector>? detectors = null, Workspace? ws = null)
    {
        ws ??= new Workspace(store, cfg);
        cfg = ws.Cfg;
        detectors ??= DefaultDetectors.Create();
        var incidents = new List<Incident>();
        for (var pass = 0; pass < MaxPasses; pass++)
        {
            var found = AnalyzeWindow(store, ws, detectors);
            incidents.AddRange(found);
            if (found.Count == 0 || found.Any(i => i.ResolvedAt is null)) break;
            var next = found.Max(i => TimeUtil.BucketOf(i.ResolvedAt!.Value)) + cfg.RebaselineGapMinutes;
            if (ws.T1 - next + 1 <= cfg.BaselineMinutes + cfg.WindowMinutes + 10) break;
            ws = new Workspace(store, cfg, next);
        }
        for (var n = 0; n < incidents.Count; n++) incidents[n].Id = $"AIL-{1001 + n}";
        return incidents;
    }
}
