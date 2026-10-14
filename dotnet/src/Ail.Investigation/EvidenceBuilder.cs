using Ail.Detection;
using Ail.Schemas;

namespace Ail.Investigation;

/// <summary>Turns an incident into numbered, citable evidence. Every telemetry fact the agent may state lives here.</summary>
public static class EvidenceBuilder
{
    private static string Hm(DateTimeOffset dt) => TimeUtil.Hhmm(TimeUtil.BucketOf(dt));

    public static List<EvidenceItem> Build(Incident inc, IReadOnlyList<CandidateCommit> candidates, int commitTotal = 0,
        string? repoName = null, (string Base, string Head)? refRange = null)
    {
        var items = new List<EvidenceItem>();

        void Add(string prefix, string kind, string statement, Dictionary<string, object?> data)
        {
            var n = items.Count(i => i.Id.StartsWith(prefix)) + 1;
            items.Add(new EvidenceItem { Id = $"{prefix}{n}", Kind = kind, Statement = statement, Data = data });
        }

        var top = inc.Cohorts.FirstOrDefault();
        var dims = top?.Dimensions ?? new Dictionary<string, string>();
        var scope = Labels.CohortLabel(dims);
        var label = Labels.MetricLabel(inc.Metric);
        var ratio = inc.Baseline != 0 ? inc.Observed / inc.Baseline : 0.0;
        var endTxt = "end of data";
        if (inc.Reproducibility.TryGetValue("scan_window", out var sw) && sw is string[] { Length: 2 } win)
            endTxt = Hm(DateTimeOffset.Parse(win[1]));

        Add("E", "anomaly",
            $"{label} in {scope} changed from {Labels.FormatValue(inc.Metric, inc.Baseline)} to {Labels.FormatValue(inc.Metric, inc.Observed)} " +
            $"({ratio:F1}x baseline) between {Hm(inc.Onset)} and {endTxt} UTC. Across all devices {label.ToLowerInvariant()} moved from " +
            $"{Labels.FormatValue(inc.Metric, inc.GlobalBaseline)} to {Labels.FormatValue(inc.Metric, inc.GlobalObserved)}.",
            new() { ["metric"] = inc.Metric, ["scope"] = scope, ["dimensions"] = dims, ["baseline"] = inc.Baseline, ["observed"] = inc.Observed, ["ratio"] = ratio, ["severity"] = inc.Severity });

        var primary = inc.Results.Where(r => r.Metric == inc.Metric).ToList();
        var detectors = primary.Select(r => r.Detector).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        var first = inc.Results.OrderBy(r => r.Timestamp).FirstOrDefault();
        var confTxt = inc.Confirmed is { } cf ? $"; anomaly confirmed {Hm(cf)}" : "";
        var firstTxt = first is not null ? $" ({first.Detector} on {Labels.MetricLabel(first.Metric).ToLowerInvariant()})" : "";
        Add("E", "timing",
            $"Estimated onset {Hm(inc.Onset)} UTC; first alarm {Hm(inc.FirstDetection)} UTC{firstTxt}{confTxt}. {detectors.Count} independent detectors fired on {label.ToLowerInvariant()}.",
            new() { ["onset"] = inc.Onset.ToString("O"), ["detected"] = inc.FirstDetection.ToString("O"), ["detectors"] = detectors });

        var alpha = inc.Reproducibility.TryGetValue("cohort_alpha", out var a) && a is double ad ? ad : 0.001;
        if (inc.Cohorts.Count > 0)
        {
            foreach (var c in inc.Cohorts.Take(3))
                Add("E", "cohort",
                    $"Cohort {Labels.CohortLabel(c.Dimensions)}: {label.ToLowerInvariant()} {Labels.FormatValue(inc.Metric, c.Baseline)} to " +
                    $"{Labels.FormatValue(inc.Metric, c.Observed)} ({c.Ratio:F1}x), {c.PopulationShare * 100:F1}% of sessions, {c.ExcessShare * 100:F0}% of the excess " +
                    $"(scan statistic, adjusted p < {alpha:G}).",
                    new() { ["dimensions"] = c.Dimensions, ["label"] = Labels.CohortLabel(c.Dimensions), ["rank"] = c.Rank, ["ratio"] = c.Ratio, ["population_share"] = c.PopulationShare, ["excess_share"] = c.ExcessShare });
        }
        else Add("E", "scope", "No specific cohort stands out: the deviation is spread across the whole population.", new());

        foreach (var s in inc.SupportingMetrics.Where(s => s["significant"] is true))
        {
            var m = (string)s["metric"]!;
            Add("E", "supporting_metric",
                $"{Labels.MetricLabel(m)} in {s["scope"]}: {Labels.FormatValue(m, (double)s["baseline"]!)} to {Labels.FormatValue(m, (double)s["observed"]!)} ({(double)s["ratio"]!:F1}x).",
                new() { ["metric"] = m, ["ratio"] = s["ratio"] });
        }

        foreach (var sg in inc.Signatures.Take(2).Where(sg => (double)sg["excess_share"]! >= 0.2))
            Add("E", "signature",
                $"Failure signature {sg["signature"]} accounts for {(double)sg["excess_share"]! * 100:F0}% of the excess failures in {scope} " +
                $"(baseline share {(double)sg["baseline_share"]! * 100:F0}% of all failures).",
                new() { ["signature"] = sg["signature"], ["excess_share"] = sg["excess_share"] });

        foreach (var c in inc.Correlations.Take(3))
        {
            var ch = c.Change;
            var basis = $"{ch.Component} {ch.FromVersion}->{ch.ToVersion} ({ch.Kind}, id {ch.DeploymentId})";
            var data = new Dictionary<string, object?>
            {
                ["deployment_id"] = ch.DeploymentId, ["component"] = ch.Component, ["kind"] = ch.Kind, ["from_version"] = ch.FromVersion,
                ["to_version"] = ch.ToVersion, ["score"] = c.Score, ["overlap"] = c.Overlap, ["after_onset"] = c.AfterOnset,
                ["repository"] = ch.Repository, ["base_ref"] = ch.BaseRef, ["head_ref"] = ch.HeadRef,
            };
            if (c.AfterOnset)
                Add("E", "correlation", $"{basis} began {Hm(ch.Timestamp)} UTC, after the anomaly onset, so it cannot be the trigger (correlation score {c.Score:F2}).", data);
            else
                Add("E", "correlation",
                    $"{basis} began {Hm(ch.Timestamp)} UTC, {Math.Max(c.DeltaMinutes, 0):F0} min before the onset; {c.Overlap * 100:F0}% overlap with the affected population; " +
                    $"correlation score {c.Score:F2}. Correlation is evidence of association, not proof of causation.", data);
        }

        foreach (var h in inc.SimilarIncidents.Take(2))
        {
            var onset = (string)h["onset"]!;
            var recurring = h["recurring"] is true;
            Add("H", "history",
                $"A similar earlier incident {h["id"]} ({h["title"]}, onset {onset[..10]} {onset[11..16]} UTC) was recorded; similarity {(double)h["score"]!:F2} across " +
                $"metric, cohort, failure signature and trigger type.{(recurring ? " This may be a recurrence." : "")}",
                new() { ["id"] = h["id"], ["score"] = h["score"], ["recurring"] = recurring, ["onset"] = onset });
        }

        foreach (var c in candidates.Take(3))
        {
            var where = repoName is not null && refRange is { } rr ? $" in {repoName} {rr.Base}..{rr.Head}" : "";
            var reasons = c.Reasons.Count > 0 ? string.Join("; ", c.Reasons) : "no strong signals";
            Add("C", "code",
                $"Commit {c.Sha[..7]} ({c.Component ?? "unknown component"}) ranks {c.Rank} of {commitTotal} commits{where} by heuristic code review (score {c.Score:F2}): {reasons}.",
                new() { ["sha"] = c.Sha, ["sha7"] = c.Sha[..7], ["component"] = c.Component, ["score"] = c.Score, ["rank"] = c.Rank, ["files"] = c.Files, ["reasons"] = c.Reasons, ["message"] = c.Message });
        }
        return items;
    }
}
