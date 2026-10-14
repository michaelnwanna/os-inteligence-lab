using Ail.Schemas;

namespace Ail.Detection;

/// <summary>Read-only analytics used by the portal: series, cohort breakdowns and population health.</summary>
public static class Explore
{
    public static Dictionary<string, object?> SeriesPayload(Workspace ws, string metric, IReadOnlyDictionary<string, string>? filters = null)
    {
        var cube = ws.Cube(metric); var scanner = ws.Scanner(metric);
        var mask = cube.Mask(filters);
        var n = new double[cube.T]; var observed = new double[cube.T]; var expected = new double[cube.T];
        for (var t = 0; t < cube.T; t++)
        {
            double sn = 0, so = 0, se = 0;
            for (var c = 0; c < cube.C; c++)
            {
                if (!mask[c]) continue;
                sn += cube.N[t][c]; so += cube.Kind == "rate" ? cube.K[t][c] : cube.S[t][c]; se += cube.N[t][c] * scanner.Level[c];
            }
            n[t] = sn;
            observed[t] = sn > 0 ? so / sn : 0; expected[t] = sn > 0 ? se / sn : 0;
        }
        return new()
        {
            ["metric"] = metric, ["t0"] = TimeUtil.Iso(cube.T0),
            ["minutes"] = Enumerable.Range(0, cube.T).Select(i => TimeUtil.Hhmm(cube.T0 + i)).ToList(),
            ["sessions"] = n, ["observed"] = observed, ["expected"] = expected, ["baseline_end"] = ws.BaselineEnd,
        };
    }

    public static Dictionary<string, object?> Breakdown(Workspace ws, string metric, IReadOnlyDictionary<string, string>? filters = null, int? windowMinutes = null)
    {
        var cube = ws.Cube(metric); var scanner = ws.Scanner(metric);
        var i1 = cube.T;
        var i0 = Math.Max(ws.BaselineEnd, i1 - (windowMinutes ?? ws.Cfg.Cohort.WindowMinutes * 3));
        var baseMask = cube.Mask(filters);
        var (nw, _, _, _) = cube.Window(i0, i1);
        var totalN = Math.Max(Enumerable.Range(0, cube.C).Where(c => baseMask[c]).Sum(c => nw[c]), 1e-12);
        var dimensions = new List<Dictionary<string, object?>>();
        for (var j = 0; j < Dimensions.All.Length; j++)
        {
            var dim = Dimensions.All[j];
            if ((filters?.ContainsKey(dim) ?? false) || cube.Vocab[dim].Count < 2) continue;
            var rows = new List<(double Llr, Dictionary<string, object?> Row)>();
            for (var code = 0; code < cube.Vocab[dim].Count; code++)
            {
                var m = Enumerable.Range(0, cube.C).Select(c => baseMask[c] && cube.Codes[c][j] == code).ToArray();
                var n = Enumerable.Range(0, cube.C).Where(c => m[c]).Sum(c => nw[c]);
                if (n <= 0) continue;
                var ev = scanner.Evaluate(m, i0, i1);
                rows.Add((ev.Llr, new() { ["value"] = cube.Vocab[dim][code], ["sessions"] = n, ["share"] = n / totalN, ["observed"] = ev.Observed, ["baseline"] = ev.Baseline, ["ratio"] = ev.Ratio, ["llr"] = ev.Llr }));
            }
            dimensions.Add(new() { ["dimension"] = dim, ["values"] = rows.OrderByDescending(r => r.Llr).Select(r => r.Row).ToList() });
        }
        var sel = scanner.Evaluate(baseMask, i0, i1);
        return new()
        {
            ["metric"] = metric, ["filters"] = filters ?? new Dictionary<string, string>(),
            ["window"] = new[] { TimeUtil.Iso(cube.T0 + i0), TimeUtil.Iso(cube.T0 + i1 - 1) },
            ["selection"] = new Dictionary<string, object?> { ["sessions"] = sel.Sessions, ["observed"] = sel.Observed, ["baseline"] = sel.Baseline, ["ratio"] = sel.Ratio, ["llr"] = sel.Llr },
            ["dimensions"] = dimensions,
        };
    }

    /// <summary>Monitored series (metric x dimension value) and how many currently deviate from baseline.</summary>
    public static Dictionary<string, object?> Health(Workspace ws, int windowMinutes = 10)
    {
        int monitored = 0, flagged = 0;
        foreach (var metric in ws.Metrics.Where(ws.Cfg.Metrics.Contains))
        {
            var cube = ws.Cube(metric); var scanner = ws.Scanner(metric);
            var i1 = cube.T; var i0 = Math.Max(ws.BaselineEnd, i1 - windowMinutes);
            var minRatio = cube.Kind == "rate" ? ws.Cfg.Cohort.MinRatioRate : ws.Cfg.Cohort.MinRatioGauge;
            bool Deviates(bool[] m) { var ev = scanner.Evaluate(m, i0, i1); return ev.Llr > 12 && ev.Ratio >= minRatio; }
            monitored++;
            if (Deviates(Enumerable.Repeat(true, cube.C).ToArray())) flagged++;
            for (var j = 0; j < Dimensions.All.Length; j++)
            {
                if (cube.Vocab[Dimensions.All[j]].Count < 2) continue;
                for (var code = 0; code < cube.Vocab[Dimensions.All[j]].Count; code++)
                {
                    monitored++;
                    if (Deviates(Enumerable.Range(0, cube.C).Select(c => cube.Codes[c][j] == code).ToArray())) flagged++;
                }
            }
        }
        return new() { ["monitored_series"] = monitored, ["deviating_series"] = flagged, ["healthy_series"] = monitored - flagged };
    }
}
