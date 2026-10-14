using Ail.Schemas;

namespace Ail.Detection;

public sealed class DetectionContext
{
    public required Dictionary<string, Series> Series { get; init; }
    public required Dictionary<string, Stats.Baseline> Baselines { get; init; }
    public required DetectionConfig Cfg { get; init; }
    public Dictionary<string, string> Dims { get; init; } = new();
}

/// <summary>
/// Every detector implements Detect(ctx). All are causal: the statistic at minute t only uses data up to t, so the first
/// alarm time is a faithful detection latency. An alarm must persist ConfirmMinutes later, which suppresses blips.
/// </summary>
public abstract class Detector
{
    public abstract string Name { get; }

    public virtual List<AnomalyResult> Detect(DetectionContext ctx)
    {
        var results = new List<AnomalyResult>();
        foreach (var (metric, series) in ctx.Series)
            if (DetectMetric(metric, series, ctx.Baselines[metric], ctx) is { } r) results.Add(r);
        return results;
    }

    protected abstract AnomalyResult? DetectMetric(string metric, Series series, Stats.Baseline b, DetectionContext ctx);
}

/// <summary>Trailing-window z-score against the baseline, with dispersion adjustment.</summary>
public sealed class ZScoreDetector : Detector
{
    public override string Name => "ZScoreDetector";

    protected override AnomalyResult? DetectMetric(string metric, Series series, Stats.Baseline b, DetectionContext ctx)
    {
        var cfg = ctx.Cfg;
        var z = Stats.TrailingZ(series, b, cfg.WindowMinutes);
        var above = z.Select(v => v > cfg.ZThreshold).ToArray();
        if (Stats.FirstPersistent(above, cfg.MinConsecutive, b.End) is not { } alarm) return null;
        var confirmed = Stats.Confirmed(above, alarm, cfg.ConfirmMinutes);
        if (confirmed == false) return null;
        var peak = z[Math.Min(alarm + cfg.ConfirmMinutes, series.T - 1)];
        var onset = Stats.EstimateOnset(series, b, b.End, alarm);
        return Stats.BuildResult(series, b, metric, Name, alarm, onset, peak / cfg.ZThreshold,
            new() { ["z"] = Math.Round(peak, 2), ["threshold"] = cfg.ZThreshold, ["window_minutes"] = cfg.WindowMinutes, ["confirmed"] = confirmed },
            cfg.ConfirmMinutes, ctx.Dims);
    }
}

/// <summary>CUSUM and EWMA control charts on per-minute standardized residuals.</summary>
public sealed class EwmaCusumDetector : Detector
{
    public override string Name => "EwmaCusumDetector";

    protected override AnomalyResult? DetectMetric(string metric, Series series, Stats.Baseline b, DetectionContext ctx)
    {
        var cfg = ctx.Cfg;
        var zb = Stats.BucketZ(series, b);
        int t = series.T, start = b.End;
        double[] cusum = new double[t], ewma = new double[t], ewmaSd = Enumerable.Repeat(1.0, t).ToArray();
        var lam = cfg.EwmaLambda;
        for (var i = start; i < t; i++)
        {
            cusum[i] = Math.Max(0.0, (i > start ? cusum[i - 1] : 0.0) + zb[i] - cfg.CusumK);
            ewma[i] = lam * zb[i] + (1 - lam) * (i > start ? ewma[i - 1] : 0.0);
            ewmaSd[i] = Math.Sqrt(lam / (2 - lam) * (1 - Math.Pow(1 - lam, 2 * (i - start + 1))));
        }
        var strength = Enumerable.Range(0, t).Select(i => Math.Max(cusum[i] / cfg.CusumH, ewma[i] / (cfg.EwmaLimit * ewmaSd[i]))).ToArray();
        var above = strength.Select(s => s > 1.0).ToArray();
        if (Stats.FirstPersistent(above, 1, start) is not { } alarm) return null;
        var confirmed = Stats.Confirmed(above, alarm, cfg.ConfirmMinutes);
        if (confirmed == false) return null;
        var lastZero = -1;
        for (var i = start; i <= alarm; i++) if (cusum[i] == 0) lastZero = i - start;
        var onset = Math.Min(start + (lastZero >= 0 ? lastZero + 1 : 0), alarm);
        var at = Math.Min(alarm + cfg.ConfirmMinutes, t - 1);
        return Stats.BuildResult(series, b, metric, Name, alarm, onset, strength[at],
            new()
            {
                ["cusum"] = Math.Round(cusum[at], 2), ["cusum_h"] = cfg.CusumH, ["ewma"] = Math.Round(ewma[at], 3),
                ["ewma_limit"] = cfg.EwmaLimit, ["confirmed"] = confirmed,
            }, cfg.ConfirmMinutes, ctx.Dims);
    }
}

/// <summary>Maximum-likelihood single change point in the post-baseline segment (causal).</summary>
public sealed class ChangePointDetector : Detector
{
    public override string Name => "ChangePointDetector";

    protected override AnomalyResult? DetectMetric(string metric, Series series, Stats.Baseline b, DetectionContext ctx)
    {
        var cfg = ctx.Cfg;
        int t = series.T, start = b.End;
        var stat = new double[t];
        var tauBest = Enumerable.Repeat(start, t).ToArray();
        for (var i = start + 1; i < t; i++)
        {
            double best = 0; var bestTau = start;
            for (var tau = start; tau < i; tau++)
            {
                var (n, k, s, _) = series.WSum(tau, i + 1);
                var obs = n > 0 ? (b.Kind == "rate" ? k / n : s / n) : 0;
                if (obs < b.Level * (1 + cfg.MinRelativeChange)) continue;
                var llr = Stats.SegmentLlr(b, n, k, s);
                if (llr > best) { best = llr; bestTau = tau; }
            }
            stat[i] = best; tauBest[i] = bestTau;
        }
        var above = stat.Select(v => v > cfg.ChangepointLlr).ToArray();
        if (Stats.FirstPersistent(above, 2, start) is not { } alarm) return null;
        var confirmed = Stats.Confirmed(above, alarm, cfg.ConfirmMinutes);
        if (confirmed == false) return null;
        var at = Math.Min(alarm + cfg.ConfirmMinutes, t - 1);
        return Stats.BuildResult(series, b, metric, Name, alarm, tauBest[at], stat[at] / cfg.ChangepointLlr,
            new() { ["llr"] = Math.Round(stat[at], 2), ["threshold"] = cfg.ChangepointLlr, ["confirmed"] = confirmed },
            cfg.ConfirmMinutes, ctx.Dims);
    }
}

/// <summary>Two-window comparison (baseline vs recent): two-proportion z for rates, Welch t for gauges.</summary>
public sealed class DistributionShiftDetector : Detector
{
    public override string Name => "DistributionShiftDetector";

    protected override AnomalyResult? DetectMetric(string metric, Series series, Stats.Baseline b, DetectionContext ctx)
    {
        var cfg = ctx.Cfg;
        int t = series.T, start = b.End;
        var (n1, k1, s1, ss1) = series.WSum(0, start);
        var z = new double[t];
        var effect = new double[t];
        for (var i = start; i < t; i++)
        {
            var (n2, k2, s2, ss2) = series.WSum(Math.Max(i - cfg.ShiftWindow + 1, start), i + 1);
            if (n2 <= 0) continue;
            if (b.Kind == "rate")
            {
                double p1 = k1 / n1, p2 = k2 / n2, p = (k1 + k2) / (n1 + n2);
                z[i] = (p2 - p1) / Math.Sqrt(Math.Max(p * (1 - p), 1e-12) * (1 / n1 + 1 / n2));
                effect[i] = p1 > 0 ? p2 / p1 : 1.0;
            }
            else
            {
                double m1 = s1 / n1, m2 = s2 / n2;
                var v1 = (ss1 - s1 * s1 / n1) / (n1 - 1);
                var v2 = Math.Max((ss2 - s2 * s2 / n2) / Math.Max(n2 - 1, 1), 1e-12);
                z[i] = (m2 - m1) / Math.Sqrt(v1 / n1 + v2 / n2);
                effect[i] = (m2 - m1) / Math.Sqrt(v1);
            }
            z[i] /= Math.Sqrt(b.Phi);
        }
        var above = z.Select(v => v > cfg.ShiftZ).ToArray();
        if (Stats.FirstPersistent(above, cfg.MinConsecutive, start) is not { } alarm) return null;
        var confirmed = Stats.Confirmed(above, alarm, cfg.ConfirmMinutes);
        if (confirmed == false) return null;
        var at = Math.Min(alarm + cfg.ConfirmMinutes, t - 1);
        var onset = Stats.EstimateOnset(series, b, start, alarm);
        return Stats.BuildResult(series, b, metric, Name, alarm, onset, z[at] / cfg.ShiftZ,
            new()
            {
                ["z"] = Math.Round(z[at], 2), ["effect"] = Math.Round(effect[at], 3),
                ["effect_kind"] = b.Kind == "rate" ? "rate_ratio" : "cohens_d", ["recent_window_minutes"] = cfg.ShiftWindow, ["confirmed"] = confirmed,
            }, cfg.ConfirmMinutes, ctx.Dims);
    }
}

/// <summary>
/// Unsupervised multivariate detector: Mahalanobis distance of the joint (rate, latency, memory) deviation vector with
/// mean and covariance fitted on the baseline window.
/// </summary>
public sealed class MlDetector : Detector
{
    public override string Name => "MLDetector";

    protected override AnomalyResult? DetectMetric(string metric, Series series, Stats.Baseline b, DetectionContext ctx) => null;

    public override List<AnomalyResult> Detect(DetectionContext ctx)
    {
        var cfg = ctx.Cfg;
        var metrics = ctx.Series.Keys.ToList();
        if (metrics.Count < 2) return new();
        var start = ctx.Baselines[metrics[0]].End;
        var t = ctx.Series[metrics[0]].T;
        var m = metrics.Count;
        var z = metrics.Select(name => Stats.TrailingZ(ctx.Series[name], ctx.Baselines[name], cfg.WindowMinutes)).ToArray(); // [metric][t]

        var trainIdx = Enumerable.Range(cfg.WindowMinutes - 1, Math.Max(start - cfg.WindowMinutes + 1, 0)).ToList();
        var mu = new double[m];
        for (var j = 0; j < m; j++) mu[j] = trainIdx.Average(i => z[j][i]);
        var cov = new double[m, m];
        for (var a = 0; a < m; a++)
            for (var c = 0; c < m; c++)
            {
                cov[a, c] = trainIdx.Sum(i => (z[a][i] - mu[a]) * (z[c][i] - mu[c])) / (trainIdx.Count - 1);
                if (a == c) cov[a, c] += 0.05;
            }
        var inv = Invert(cov, m);
        var d2 = new double[t];
        var above = new bool[t];
        for (var i = 0; i < t; i++)
        {
            var d = new double[m];
            for (var j = 0; j < m; j++) d[j] = z[j][i] - mu[j];
            double q = 0;
            for (var a = 0; a < m; a++) for (var c = 0; c < m; c++) q += d[a] * inv[a, c] * d[c];
            d2[i] = q;
            above[i] = i >= start && q > cfg.MlThreshold && Enumerable.Range(0, m).Max(j => z[j][i]) > 0;
        }
        if (Stats.FirstPersistent(above, cfg.MinConsecutive, start) is not { } alarm) return new();
        var confirmed = Stats.Confirmed(above, alarm, cfg.ConfirmMinutes);
        if (confirmed == false) return new();
        var at = Math.Min(alarm + cfg.ConfirmMinutes, t - 1);
        var dom = Enumerable.Range(0, m).MaxBy(j => z[j][alarm]);
        var name = metrics[dom];
        var series = ctx.Series[name];
        var onset = Stats.EstimateOnset(series, ctx.Baselines[name], start, alarm);
        var comps = metrics.Select((n, j) => (n, v: Math.Round(z[j][at], 2))).ToDictionary(x => x.n, x => (object?)x.v);
        return new()
        {
            Stats.BuildResult(series, ctx.Baselines[name], name, Name, alarm, onset, d2[at] / cfg.MlThreshold,
                new() { ["mahalanobis_sq"] = Math.Round(d2[at], 1), ["threshold"] = cfg.MlThreshold, ["component_z"] = comps, ["confirmed"] = confirmed },
                cfg.ConfirmMinutes, ctx.Dims),
        };
    }

    /// <summary>Gauss-Jordan inverse of a small symmetric positive-definite matrix.</summary>
    private static double[,] Invert(double[,] a, int n)
    {
        var aug = new double[n, 2 * n];
        for (var i = 0; i < n; i++) { for (var j = 0; j < n; j++) aug[i, j] = a[i, j]; aug[i, n + i] = 1; }
        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            for (var r = col + 1; r < n; r++) if (Math.Abs(aug[r, col]) > Math.Abs(aug[pivot, col])) pivot = r;
            if (pivot != col) for (var j = 0; j < 2 * n; j++) (aug[col, j], aug[pivot, j]) = (aug[pivot, j], aug[col, j]);
            var div = aug[col, col];
            for (var j = 0; j < 2 * n; j++) aug[col, j] /= div;
            for (var r = 0; r < n; r++)
            {
                if (r == col) continue;
                var f = aug[r, col];
                if (f == 0) continue;
                for (var j = 0; j < 2 * n; j++) aug[r, j] -= f * aug[col, j];
            }
        }
        var inv = new double[n, n];
        for (var i = 0; i < n; i++) for (var j = 0; j < n; j++) inv[i, j] = aug[i, n + j];
        return inv;
    }
}

public static class DefaultDetectors
{
    public static List<Detector> Create() => new()
    {
        new ZScoreDetector(), new EwmaCusumDetector(), new ChangePointDetector(), new DistributionShiftDetector(), new MlDetector(),
    };
}
