using Ail.Detection;
using Ail.Schemas;

namespace Ail.Investigation;

/// <summary>Failure Time Machine: the replayable story of an incident, from deployment to investigation.</summary>
public static class TimeMachine
{
    private static (ChangeEvent Change, double Score)? LeadChange(Incident inc)
    {
        var c = inc.Correlations.FirstOrDefault(c => !c.AfterOnset && c.Score >= 0.3);
        return c is null ? null : (c.Change, c.Score);
    }

    private static double[] PerMinute(double[] num, double[] den) => num.Select((v, i) => den[i] > 0 ? v / den[i] : 0.0).ToArray();

    public static (List<TimelineEvent> Events, Dictionary<string, object?> Data) Build(Workspace ws, Incident inc)
    {
        var cfg = ws.Cfg;
        var cube = ws.Cube(inc.Metric); var scanner = ws.Scanner(inc.Metric);
        var dims = inc.Cohorts.FirstOrDefault()?.Dimensions ?? new Dictionary<string, string>();
        var mask = cube.Mask(dims);
        var start = Math.Max(0, ws.BaselineEnd - 10);
        int T = cube.T;

        double[] Sum(double[][] a, bool[]? m = null) => a.Select(row => Enumerable.Range(0, cube.C).Where(c => m is null || m[c]).Sum(c => row[c])).ToArray();
        var nAll = Sum(cube.N);
        var obsCol = cube.Kind == "rate" ? cube.K : cube.S;
        var nMask = Sum(cube.N, mask);
        var expMask = cube.N.Select(row => Enumerable.Range(0, cube.C).Where(c => mask[c]).Sum(c => row[c] * scanner.Level[c])).ToArray();
        var frames = new Dictionary<string, object?>
        {
            ["global"] = PerMinute(Sum(obsCol), nAll).Skip(start).ToArray(),
            ["cohort"] = PerMinute(Sum(obsCol, mask), nMask).Skip(start).ToArray(),
            ["expected"] = PerMinute(expMask, nMask).Skip(start).ToArray(),
            ["cohort_sessions"] = nMask.Skip(start).ToArray(),
        };
        var lead = LeadChange(inc);
        double[]? exposure = null;
        if (lead is { Change: { Dimension: not null, ToVersion: not null } } lc)
        {
            var touched = cube.Mask(new Dictionary<string, string> { [lc.Change.Dimension!] = lc.Change.ToVersion! });
            exposure = PerMinute(Sum(cube.N, touched), nAll);
            frames["exposure"] = exposure.Skip(start).ToArray();
        }
        var secondary = inc.SupportingMetrics.FirstOrDefault(s => s["significant"] is true);
        if (secondary is not null)
        {
            var m2Name = (string)secondary["metric"]!;
            var c2 = ws.Cube(m2Name); var m2 = c2.Mask(dims);
            frames["secondary"] = new Dictionary<string, object?>
            {
                ["metric"] = m2Name, ["label"] = Labels.MetricLabel(m2Name),
                ["values"] = PerMinute(c2.S.Select(row => Enumerable.Range(0, c2.C).Where(c => m2[c]).Sum(c => row[c])).ToArray(),
                    c2.N.Select(row => Enumerable.Range(0, c2.C).Where(c => m2[c]).Sum(c => row[c])).ToArray()).Skip(start).ToArray(),
            };
        }

        var stage = TimeSpan.FromMinutes(cfg.PipelineStageMinutes);
        var first = inc.Results.OrderBy(r => r.Timestamp).FirstOrDefault();
        var events = new List<TimelineEvent>();
        void Add(DateTimeOffset ts, string kind, string title, string detail = "", bool simulated = false, Dictionary<string, object?>? evidence = null) =>
            events.Add(new TimelineEvent { Ts = ts, Kind = kind, Title = title, Detail = detail, SimulatedLatency = simulated, Evidence = evidence ?? new() });

        if (lead is { } l)
        {
            var ch = l.Change;
            Add(ch.Timestamp, "deployment_started", $"{ch.Component} {ch.ToVersion} {ch.Kind.Replace('_', ' ')} started".Replace("  ", " "), ch.Description,
                evidence: new() { ["deployment_id"] = ch.DeploymentId, ["correlation_score"] = l.Score });
            if (exposure is not null)
            {
                var from = (int)(TimeUtil.BucketOf(ch.Timestamp) - ws.T0);
                int? hit = null;
                for (var i = 0; i < exposure.Length; i++) if (i >= from && exposure[i] >= 0.05) { hit = i; break; }
                if (hit is { } h)
                    Add(TimeUtil.DtOf(ws.T0 + h), "exposure", "5% of sessions exposed to the change", $"{exposure[h] * 100:F1}% of sessions now run {ch.ToVersion}",
                        evidence: new() { ["exposure"] = exposure[h] });
            }
        }
        Add(inc.Onset, "first_abnormal", $"First abnormal {Labels.MetricLabel(inc.Metric).ToLowerInvariant()} (estimated onset)", $"Change-point estimate for {Labels.CohortLabel(dims)}",
            evidence: new() { ["scope"] = Labels.CohortLabel(dims) });
        Add(inc.FirstDetection, "threshold_exceeded", "Statistical threshold exceeded",
            first is not null ? $"{first.Detector} on {Labels.MetricLabel(first.Metric).ToLowerInvariant()}" : "",
            evidence: new() { ["detectors"] = inc.Results.Select(r => r.Detector).ToList() });
        if (inc.Confirmed is { } conf) Add(conf, "anomaly_confirmed", "Anomaly confirmed", "Alarm persisted for the confirmation interval");
        var isolated = inc.Results.Where(r => r.Detector == "CohortScanDetector").Select(r => (DateTimeOffset?)r.Timestamp).Min();
        if (inc.Cohorts.Count > 0 && isolated is { } iso)
        {
            var c = inc.Cohorts[0];
            Add(iso, "cohort_isolated", $"{Labels.CohortLabel(c.Dimensions)} cohort isolated",
                $"{Labels.MetricLabel(inc.Metric)} {c.Ratio:F1}x baseline in {c.PopulationShare * 100:F1}% of sessions", evidence: new() { ["dimensions"] = c.Dimensions, ["ratio"] = c.Ratio });
        }
        var anchor = new[] { inc.Confirmed, isolated, inc.FirstDetection }.Where(x => x is not null).Max()!.Value;
        var steps = 1;
        if (lead is { } l2)
        {
            Add(anchor + stage * steps, "correlation_discovered", $"Correlation discovered: {l2.Change.Component} {l2.Change.ToVersion}".Trim(),
                "Ranked by temporal proximity and population overlap (correlation, not causation)", true,
                new() { ["score"] = l2.Score, ["deployment_id"] = l2.Change.DeploymentId });
            steps++;
        }
        var inv = inc.Investigation;
        if (inv is { CandidateCommits.Count: > 0 })
        {
            var top = inv.CandidateCommits[0];
            Add(anchor + stage * steps, "commits_analyzed", "GitHub commits analyzed", $"{inv.CandidateCommits.Count} candidates ranked; top: {top.Sha[..7]} ({top.Component})", true,
                new() { ["sha"] = top.Sha, ["score"] = top.Score });
            steps++;
        }
        if (inv is not null)
            Add(anchor + stage * steps, "ai_completed", "AI investigation completed", inv.Hypotheses.Count > 0 ? inv.Hypotheses[0].Title : "", true, new() { ["provider"] = inv.Provider });
        if (inc.ResolvedAt is { } resolved)
            Add(resolved, "recovered", $"{Labels.MetricLabel(inc.Metric)} recovered", $"No significant excess for {cfg.RecoveryMinutes} minutes in {Labels.CohortLabel(dims)}");
        events = events.OrderBy(e => e.Ts).ToList();

        var lastTs = TimeUtil.DtOf(ws.T0 + T - 1);
        var startDt = TimeUtil.DtOf(ws.T0 + start);
        var data = new Dictionary<string, object?>
        {
            ["metric"] = inc.Metric, ["label"] = Labels.MetricLabel(inc.Metric), ["scope"] = Labels.CohortLabel(dims),
            ["start"] = startDt.ToString("O"),
            ["minutes"] = Enumerable.Range(0, T - start).Select(i => TimeUtil.Hhmm(ws.T0 + start + i)).ToList(),
            ["frames"] = frames,
            ["events"] = events.Select(e => new Dictionary<string, object?>
            {
                ["index"] = Math.Clamp((int)Math.Floor((e.Ts - startDt).TotalMinutes), 0, T - start - 1), ["after_data"] = e.Ts > lastTs,
                ["ts"] = e.Ts, ["kind"] = e.Kind, ["title"] = e.Title, ["detail"] = e.Detail, ["simulated_latency"] = e.SimulatedLatency, ["evidence"] = e.Evidence,
            }).ToList(),
        };
        return (events, data);
    }
}
