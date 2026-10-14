using Ail.Schemas;

namespace Ail.Detection;

public readonly record struct CohortEval(double Sessions, double Observed, double Baseline, double Ratio, double Llr);

/// <summary>
/// Cohort intelligence: an expectation-based scan statistic over multi-dimensional cohorts. Expectations come from an
/// empirical-Bayes hierarchy fitted on the baseline window, so a cell that did not exist then (a new version) is compared
/// with the hardware/OS group it belongs to. Candidates are ranked by a log-likelihood ratio, penalised for extra
/// dimensions; the best cohort is explained away before the next search, and a backfitting pass untangles overlaps.
/// </summary>
public sealed class CohortScanner
{
    // Coarse -> fine; each level retains a superset of the previous level's dimensions.
    private static readonly string[][] Hierarchy =
    {
        new[] { "architecture" },
        new[] { "architecture", "device_model", "application" },
        new[] { "os_version", "architecture", "device_model", "application" },
        new[] { "os_version", "architecture", "device_model", "application", "driver_version" },
        Dimensions.All,
    };

    private readonly Cube _cube;
    private readonly CohortConfig _cfg;
    private readonly List<(int[] Combo, int[] Inv, int G)> _groups = new();
    private readonly int _nCandidates;

    public int BaselineEnd { get; }
    public double[] Level { get; }
    public double Sigma2 { get; private set; } = 1.0;

    public CohortScanner(Cube cube, int baselineEnd, CohortConfig cfg)
    {
        if (cube.Kind == "count") throw new ArgumentException("cohort scanning needs a rate or gauge metric");
        _cube = cube; _cfg = cfg; BaselineEnd = baselineEnd;
        var (nb, kb, sb, ssb) = cube.Window(0, baselineEnd);
        Level = ExpectedLevel(nb, cube.Kind == "rate" ? kb : sb);
        if (cube.Kind == "gauge")
        {
            double dof = 0, ssum = 0; var cellsWith = 0;
            for (var c = 0; c < cube.C; c++)
            {
                if (nb[c] <= 1) continue;
                cellsWith++; dof += nb[c]; ssum += ssb[c] - sb[c] * sb[c] / nb[c];
            }
            Sigma2 = Math.Max(ssum / Math.Max(dof - cellsWith, 1.0), 1e-12);
        }
        var dims = Enumerable.Range(0, Dimensions.All.Length).Where(j => cube.Vocab[Dimensions.All[j]].Count > 1).ToArray();
        for (var depth = 1; depth <= cfg.MaxDepth; depth++)
            foreach (var combo in Combinations(dims, depth))
            {
                var (inv, g) = GroupIndex(combo);
                _groups.Add((combo, inv, g));
            }
        _nCandidates = Math.Max(_groups.Sum(x => x.G), 1);
    }

    private static IEnumerable<int[]> Combinations(int[] items, int k)
    {
        var idx = new int[k];
        IEnumerable<int[]> Recurse(int start, int depth)
        {
            if (depth == k) { yield return (int[])idx.Clone(); yield break; }
            for (var i = start; i < items.Length; i++) { idx[depth] = items[i]; foreach (var c in Recurse(i + 1, depth + 1)) yield return c; }
        }
        return Recurse(0, 0);
    }

    private (int[] Inv, int G) GroupIndex(int[] columns)
    {
        var ids = new Dictionary<string, int>();
        var inv = new int[_cube.C];
        for (var c = 0; c < _cube.C; c++)
        {
            var key = string.Join(",", columns.Select(j => _cube.Codes[c][j]));
            if (!ids.TryGetValue(key, out var id)) ids[key] = id = ids.Count;
            inv[c] = id;
        }
        return (inv, ids.Count);
    }

    private double[] ExpectedLevel(double[] nb, double[] vb)
    {
        var prior = _cfg.PriorSessionsRate;
        if (_cube.Kind != "rate") prior = _cfg.PriorSessionsGauge;
        var level = Enumerable.Repeat(vb.Sum() / Math.Max(nb.Sum(), 1.0), _cube.C).ToArray();
        foreach (var dims in Hierarchy)
        {
            var (inv, g) = GroupIndex(dims.Select(Dimensions.IndexOf).ToArray());
            double[] ng = new double[g], vg = new double[g], parent = new double[g];
            for (var c = 0; c < _cube.C; c++) { ng[inv[c]] += nb[c]; vg[inv[c]] += vb[c]; parent[inv[c]] = level[c]; }
            for (var c = 0; c < _cube.C; c++) level[c] = (vg[inv[c]] + prior * parent[inv[c]]) / (ng[inv[c]] + prior);
        }
        return level;
    }

    public List<(CohortFinding Finding, bool[] Mask)> Scan(int i0, int i1, int? topK = null, bool[]? exclude = null, bool refine = true)
    {
        var cube = _cube; var cfg = _cfg; var C = cube.C;
        var (nw, kw, sw, _) = cube.Window(i0, i1);
        var N = (double[])nw.Clone();
        var O = (double[])(cube.Kind == "rate" ? kw : sw).Clone();
        var E = new double[C];
        for (var c = 0; c < C; c++) E[c] = nw[c] * Level[c];
        var totalN = N.Sum();
        var totalExcess = O.Sum() - E.Sum();
        var claimed = exclude is null ? new bool[C] : (bool[])exclude.Clone();
        for (var c = 0; c < C; c++) if (claimed[c]) N[c] = O[c] = E[c] = 0;
        var fullMasks = new List<bool[]>();
        var output = new List<(CohortFinding, bool[])>();

        for (var rank = 1; rank <= (topK ?? cfg.TopK); rank++)
        {
            double ot = O.Sum(), et = E.Sum();
            (double Score, double Llr, int[] Combo, int J, int[] Inv, double Ng, double Og, double Eg)? best = null;
            foreach (var (combo, inv, g) in _groups)
            {
                double[] ng = new double[g], og = new double[g], eg = new double[g];
                for (var c = 0; c < C; c++) { ng[inv[c]] += N[c]; og[inv[c]] += O[c]; eg[inv[c]] += E[c]; }
                for (var j = 0; j < g; j++)
                {
                    if (eg[j] <= 0) continue;
                    var outRatio = et - eg[j] > 1e-9 ? (ot - og[j]) / Math.Max(et - eg[j], 1e-9) : 1.0;
                    var adj = Math.Max(eg[j] * Math.Max(outRatio, 1.0), 1e-12);
                    if (cube.Kind == "rate") adj *= 1 + cfg.ExpectationMargin;
                    if (og[j] <= adj) continue;
                    var ratio = og[j] / adj;
                    double llr; bool ok;
                    if (cube.Kind == "rate")
                    {
                        llr = og[j] * Math.Log(Math.Max(og[j], 1e-12) / adj) - og[j] + adj;
                        ok = og[j] >= cfg.MinEvents && ratio >= cfg.MinRatioRate;
                    }
                    else
                    {
                        llr = (og[j] - adj) * (og[j] - adj) / (2 * Sigma2 * Math.Max(ng[j], 1.0));
                        ok = ng[j] >= 200 && ratio >= cfg.MinRatioGauge;
                    }
                    if (!ok) continue;
                    var score = llr - cfg.DepthPenalty * (combo.Length - 1);
                    if (best is null || score > best.Value.Score) best = (score, llr, combo, j, inv, ng[j], og[j], eg[j]);
                }
            }
            if (best is null) break;
            var (_, llrV, bcombo, bj, binv, bng, bog, beg) = best.Value;
            var p = Math.Min(1.0, _nCandidates * 0.5 * Stats.Erfc(Math.Sqrt(Math.Max(llrV, 0.0))));
            if (p > cfg.Alpha) break;

            var mask = new bool[C]; var full = new bool[C];
            var first = -1;
            for (var c = 0; c < C; c++)
            {
                full[c] = binv[c] == bj;
                mask[c] = full[c] && !claimed[c];
                if (mask[c] && first < 0) first = c;
            }
            fullMasks.Add(full);
            var dims = bcombo.ToDictionary(col => Dimensions.All[col], col => cube.Vocab[Dimensions.All[col]][cube.Codes[first][col]]);
            double observed = bog / bng, baseline = beg / bng;
            var ratioV = baseline > 0 ? observed / baseline : double.PositiveInfinity;
            var (severity, _) = Stats.SeverityFor(ratioV, cube.Kind);
            output.Add((new CohortFinding
            {
                Metric = cube.Metric, Dimensions = dims, Sessions = bng, Baseline = baseline, Observed = observed, Ratio = ratioV,
                Delta = observed - baseline, Llr = llrV, PValue = p, Severity = severity, PopulationShare = bng / Math.Max(totalN, 1.0),
                ExcessShare = totalExcess > 0 ? Math.Clamp((bog - beg) / totalExcess, 0.0, 1.0) : 0.0, Rank = rank,
            }, mask));
            for (var c = 0; c < C; c++) if (mask[c]) { N[c] = O[c] = E[c] = 0; claimed[c] = true; }
        }

        if (refine && output.Count >= 2)
        {
            // backfit: re-pick each cohort with the others' cells removed, so overlapping regressions are not blended
            for (var i = 0; i < output.Count; i++)
            {
                var others = new bool[C];
                for (var j = 0; j < fullMasks.Count; j++)
                    if (j != i) for (var c = 0; c < C; c++) others[c] |= fullMasks[j][c];
                var again = Scan(i0, i1, 1, others, refine: false);
                if (again.Count == 0) continue;
                again[0].Finding.Rank = output[i].Item1.Rank;
                output[i] = again[0];
                fullMasks[i] = again[0].Mask;
            }
        }
        return output;
    }

    public CohortEval Evaluate(bool[] mask, int i0, int i1)
    {
        var (nw, kw, sw, _) = _cube.Window(i0, i1);
        double n = 0, o = 0, e = 0;
        for (var c = 0; c < _cube.C; c++)
        {
            if (!mask[c]) continue;
            n += nw[c]; o += _cube.Kind == "rate" ? kw[c] : sw[c]; e += nw[c] * Level[c];
        }
        if (n <= 0) return new(0, 0, 0, 1, 0);
        double llr;
        if (o <= e || e <= 0) llr = 0;
        else if (_cube.Kind == "rate") llr = o * Math.Log(o / e) - o + e;
        else llr = (o - e) * (o - e) / (2 * Sigma2 * n);
        return new(n, o / n, e / n, e > 0 ? o / e : 1.0, llr);
    }

    /// <summary>Change-point estimate of when the cohort's excess started (index in [baselineEnd, end]).</summary>
    public int Onset(bool[] mask, int end, int minLen = 2)
    {
        var cube = _cube; var t = cube.T;
        double[] pn = new double[t + 1], pe = new double[t + 1], po = new double[t + 1];
        for (var i = 0; i < t; i++)
        {
            double n = 0, e = 0, o = 0;
            for (var c = 0; c < cube.C; c++)
            {
                if (!mask[c]) continue;
                n += cube.N[i][c]; e += cube.N[i][c] * Level[c]; o += cube.Kind == "rate" ? cube.K[i][c] : cube.S[i][c];
            }
            pn[i + 1] = pn[i] + n; pe[i + 1] = pe[i] + e; po[i + 1] = po[i] + o;
        }
        var lo = BaselineEnd; var hi = Math.Max(BaselineEnd, end - minLen + 1);
        var best = lo; var bestLlr = double.NegativeInfinity;
        for (var tau = lo; tau <= hi; tau++)
        {
            double n = pn[end + 1] - pn[tau], e = pe[end + 1] - pe[tau], o = po[end + 1] - po[tau];
            double llr = 0;
            if (o > e)
                llr = cube.Kind == "rate" ? o * Math.Log(Math.Max(o, 1e-12) / Math.Max(e, 1e-12)) - o + e : (o - e) * (o - e) / (2 * Sigma2 * Math.Max(n, 1.0));
            if (llr >= bestLlr) { bestLlr = llr; best = tau; }
        }
        return best;
    }

    /// <summary>Causal scan: first minute at which the same significant cohort tops the scan on `Consecutive` successive minutes.</summary>
    public (int Index, CohortFinding Finding, bool[] Mask)? FirstAlarm(int? start = null, bool[]? exclude = null)
    {
        string? prev = null; var run = 0;
        for (var i = start ?? BaselineEnd; i < _cube.T; i++)
        {
            var i0 = Math.Max(BaselineEnd, i - _cfg.WindowMinutes + 1);
            var res = Scan(i0, i + 1, 1, exclude);
            if (res.Count == 0) { prev = null; run = 0; continue; }
            var (finding, mask) = res[0];
            var key = string.Join(";", finding.Dimensions.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value));
            run = key == prev ? run + 1 : 1;
            prev = key;
            if (run >= _cfg.Consecutive) return (i, finding, mask);
        }
        return null;
    }
}
