using Ail.Schemas;

namespace Ail.Detection;

/// <summary>Statistical building blocks shared by the detectors.</summary>
public static class Stats
{
    public static (string Severity, double Score) SeverityFor(double ratio, string kind)
    {
        var (crit, high, mod) = kind is "rate" or "count" ? (5.0, 2.5, 1.25) : (2.0, 1.5, 1.2);
        var score = ratio <= 1 ? 0.0 : Math.Min(1.0, Math.Log(ratio) / Math.Log(crit));
        var level = ratio >= crit ? "critical" : ratio >= high ? "high" : ratio >= mod ? "moderate" : "low";
        return (level, score);
    }

    /// <summary>Saturating map of (statistic / threshold): 1.0 gives 0.50, 2.0 gives 0.80, 3.0 gives 0.90.</summary>
    public static double ConfidenceFromStrength(double strength)
    {
        var s = Math.Max(strength, 0.0);
        return Math.Round(Math.Min(0.99, s * s / (1.0 + s * s)), 4);
    }

    public static double Erfc(double x)
    {
        // Numerical Recipes Chebyshev approximation, fractional error below 1.2e-7
        var z = Math.Abs(x);
        var t = 1.0 / (1.0 + 0.5 * z);
        var r = t * Math.Exp(-z * z - 1.26551223 + t * (1.00002368 + t * (0.37409196 + t * (0.09678418 + t * (-0.18628806 +
                t * (0.27886807 + t * (-1.13520398 + t * (1.48851587 + t * (-0.82215223 + t * 0.17087277)))))))));
        return x >= 0 ? r : 2.0 - r;
    }

    public sealed class Baseline
    {
        public string Kind { get; init; } = "rate";
        public double Level { get; init; }   // p0 for rates, mean for gauges
        public double Sigma { get; init; }   // session-level standard deviation (gauges)
        public double Phi { get; init; }     // over-dispersion factor estimated on the baseline window
        public double N { get; init; }
        public int End { get; init; }        // first index after the baseline window
    }

    public static Baseline FitBaseline(Series series, int end)
    {
        var (n, k, s, ss) = series.WSum(0, end);
        var zs = new List<double>();
        double level, sigma = 0;
        if (series.Kind == "rate")
        {
            level = Math.Max(k / n, 1e-6);
            for (var t = 0; t < end; t++)
                if (series.N[t] > 0) zs.Add((series.K[t] - series.N[t] * level) / Math.Sqrt(series.N[t] * level * (1 - level)));
        }
        else
        {
            level = s / n;
            sigma = Math.Sqrt(Math.Max((ss - s * s / n) / Math.Max(n - 1, 1), 1e-12));
            for (var t = 0; t < end; t++)
                if (series.N[t] > 0) zs.Add((series.S[t] / series.N[t] - level) * Math.Sqrt(series.N[t]) / sigma);
        }
        var phi = zs.Count > 2 ? Math.Clamp(Variance(zs), 1.0, 25.0) : 1.0;
        return new Baseline { Kind = series.Kind, Level = level, Sigma = sigma, Phi = phi, N = n, End = end };
    }

    private static double Variance(IReadOnlyList<double> xs)
    {
        var mean = xs.Average();
        return xs.Sum(x => (x - mean) * (x - mean)) / (xs.Count - 1);
    }

    /// <summary>Dispersion-adjusted z-score of the rate/mean over [i0, i1) against the baseline.</summary>
    public static double WindowZ(Series series, Baseline b, int i0, int i1)
    {
        var (n, k, s, _) = series.WSum(i0, i1);
        if (n <= 0) return 0;
        var z = b.Kind == "rate"
            ? (k - n * b.Level) / Math.Sqrt(n * b.Level * (1 - b.Level))
            : (s / n - b.Level) * Math.Sqrt(n) / b.Sigma;
        return z / Math.Sqrt(b.Phi);
    }

    public static double[] BucketZ(Series series, Baseline b) =>
        Enumerable.Range(0, series.T).Select(i => WindowZ(series, b, i, i + 1)).ToArray();

    public static double[] TrailingZ(Series series, Baseline b, int width) =>
        Enumerable.Range(0, series.T).Select(i => WindowZ(series, b, Math.Max(i - width + 1, 0), i + 1)).ToArray();

    /// <summary>Log-likelihood ratio of "segment differs (upward)" against "segment equals baseline".</summary>
    public static double SegmentLlr(Baseline b, double n, double k, double s)
    {
        if (n <= 0) return 0;
        if (b.Kind == "rate")
        {
            var r = Math.Clamp(k / n, 1e-12, 1 - 1e-12);
            if (r <= b.Level) return 0;
            var llr = k * Math.Log(r / b.Level) + (n - k) * Math.Log((1 - r) / (1 - b.Level));
            return llr / b.Phi;
        }
        var m = s / n;
        return m > b.Level ? n * (m - b.Level) * (m - b.Level) / (2 * b.Sigma * b.Sigma) / b.Phi : 0;
    }

    /// <summary>Index in [start, end] maximising the change-point LLR of the segment [tau, end]; ties pick the latest.</summary>
    public static int EstimateOnset(Series series, Baseline b, int start, int end, int minLen = 2)
    {
        var last = Math.Max(start, end - minLen + 1);
        var best = start;
        var bestLlr = double.NegativeInfinity;
        for (var tau = start; tau <= last; tau++)
        {
            var (n, k, s, _) = series.WSum(tau, end + 1);
            var llr = SegmentLlr(b, n, k, s);
            if (llr >= bestLlr) { bestLlr = llr; best = tau; }
        }
        return best;
    }

    /// <summary>Index at which `need` consecutive true values (at or after start) are first completed.</summary>
    public static int? FirstPersistent(bool[] above, int need, int start)
    {
        var run = 0;
        for (var i = start; i < above.Length; i++)
        {
            run = above[i] ? run + 1 : 0;
            if (run >= need) return i;
        }
        return null;
    }

    /// <summary>Whether the alarm statistic is still above threshold `confirm` minutes later; null if not yet known.</summary>
    public static bool? Confirmed(bool[] above, int alarm, int confirm) =>
        alarm + confirm >= above.Length ? null : above[alarm + confirm];

    public static AnomalyResult BuildResult(Series series, Baseline b, string metric, string detector, int alarm, int onset,
        double strength, Dictionary<string, object?> evidence, int confirm, Dictionary<string, string>? dims = null, double affected = 1.0)
    {
        var end = Math.Min(alarm + confirm, series.T - 1);
        var observed = series.Value(onset, end + 1);
        var ratio = b.Level != 0 ? observed / b.Level : 1.0;
        var (severity, score) = SeverityFor(ratio, series.Kind);
        var ev = new Dictionary<string, object?>
        {
            ["alarm_minute_index"] = alarm,
            ["onset_minute_index"] = onset,
            ["strength"] = Math.Round(strength, 3),
            ["ratio"] = Math.Round(ratio, 4),
            ["baseline_sessions"] = b.N,
            ["dispersion_phi"] = Math.Round(b.Phi, 3),
        };
        foreach (var (key, value) in evidence) ev[key] = value;
        return new AnomalyResult
        {
            Metric = metric, Timestamp = TimeUtil.DtOf(series.T0 + alarm), Onset = TimeUtil.DtOf(series.T0 + onset),
            Baseline = b.Level, ObservedValue = observed, Delta = observed - b.Level, Severity = severity,
            SeverityScore = Math.Round(score, 4), Confidence = ConfidenceFromStrength(strength), AffectedPopulation = affected,
            Detector = detector, Dimensions = dims ?? new(), Evidence = ev,
        };
    }
}
