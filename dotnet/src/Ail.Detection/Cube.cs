using Ail.Schemas;

namespace Ail.Detection;

/// <summary>Per-minute sums for a selection of cells. Rate: (n, k). Gauge: (n, s, ss).</summary>
public sealed class Series
{
    public string Kind { get; }
    public long T0 { get; }
    public double[] N { get; }
    public double[] K { get; }
    public double[] S { get; }
    public double[] Ss { get; }
    private readonly double[] _pn, _pk, _ps, _pss;

    public Series(string kind, long t0, double[] n, double[] k, double[] s, double[] ss)
    {
        Kind = kind; T0 = t0; N = n; K = k; S = s; Ss = ss;
        _pn = Prefix(n); _pk = Prefix(k); _ps = Prefix(s); _pss = Prefix(ss);
    }

    private static double[] Prefix(double[] a)
    {
        var p = new double[a.Length + 1];
        for (var i = 0; i < a.Length; i++) p[i + 1] = p[i] + a[i];
        return p;
    }

    public int T => N.Length;

    /// <summary>Sums over [i0, i1). Indices are clamped to the series.</summary>
    public (double N, double K, double S, double Ss) WSum(int i0, int i1)
    {
        i0 = Math.Clamp(i0, 0, T); i1 = Math.Clamp(i1, 0, T);
        if (i1 < i0) i1 = i0;
        return (_pn[i1] - _pn[i0], _pk[i1] - _pk[i0], _ps[i1] - _ps[i0], _pss[i1] - _pss[i0]);
    }

    public double Value(int i0, int i1)
    {
        var (n, k, s, _) = WSum(i0, i1);
        if (n <= 0) return 0;
        return Kind == "rate" ? k / n : s / n;
    }
}

/// <summary>Dense (time x cohort-cell) view of one metric.</summary>
public sealed class Cube
{
    public string Metric { get; }
    public string Kind { get; }
    public long T0 { get; }
    public int[][] Codes { get; }              // [cell][dimension]
    public Dictionary<string, List<string>> Vocab { get; }
    public double[][] N { get; }               // [t][cell]
    public double[][] K { get; }
    public double[][] S { get; }
    public double[][] Ss { get; }
    private readonly double[][] _pn, _pk, _ps, _pss; // [t+1][cell]

    private Cube(string metric, string kind, long t0, int[][] codes, Dictionary<string, List<string>> vocab,
        double[][] n, double[][] k, double[][] s, double[][] ss)
    {
        Metric = metric; Kind = kind; T0 = t0; Codes = codes; Vocab = vocab; N = n; K = k; S = s; Ss = ss;
        _pn = Prefix(n); _pk = Prefix(k); _ps = Prefix(s); _pss = Prefix(ss);
    }

    private static double[][] Prefix(double[][] a)
    {
        var t = a.Length;
        var c = t == 0 ? 0 : a[0].Length;
        var p = new double[t + 1][];
        p[0] = new double[c];
        for (var i = 0; i < t; i++)
        {
            p[i + 1] = new double[c];
            for (var j = 0; j < c; j++) p[i + 1][j] = p[i][j] + a[i][j];
        }
        return p;
    }

    public int T => N.Length;
    public int C => Codes.Length;

    public static string KindOf(string metric) => metric == "failure_rate" ? "rate" : metric.StartsWith("sig:") ? "count" : "gauge";

    public static Cube FromRows(string metric, IReadOnlyList<AggRow> rows, long t0, long t1)
    {
        var T = (int)(t1 - t0 + 1);
        var cellIds = new Dictionary<string, int>();
        var cellDims = new List<string[]>();
        var cells = new List<(int B, int C, AggRow R)>();
        foreach (var r in rows)
        {
            var b = (int)(r.Bucket - t0);
            if (b < 0 || b >= T) continue;
            var key = string.Join("\u0001", r.Dims);
            if (!cellIds.TryGetValue(key, out var c)) { c = cellIds.Count; cellIds[key] = c; cellDims.Add(r.Dims); }
            cells.Add((b, c, r));
        }
        if (cells.Count == 0) throw new InvalidOperationException($"no data for metric '{metric}' in the requested window");
        var C = cellIds.Count;
        double[][] Make() => Enumerable.Range(0, T).Select(_ => new double[C]).ToArray();
        double[][] n = Make(), k = Make(), s = Make(), ss = Make();
        foreach (var (b, c, r) in cells) { n[b][c] += r.N; k[b][c] += r.K; s[b][c] += r.S; ss[b][c] += r.Ss; }
        var vocab = Dimensions.All.ToDictionary(d => d, _ => new List<string>());
        var codes = new int[C][];
        for (var c = 0; c < C; c++)
        {
            codes[c] = new int[Dimensions.All.Length];
            for (var j = 0; j < Dimensions.All.Length; j++)
            {
                var v = vocab[Dimensions.All[j]];
                var idx = v.IndexOf(cellDims[c][j]);
                if (idx < 0) { v.Add(cellDims[c][j]); idx = v.Count - 1; }
                codes[c][j] = idx;
            }
        }
        return new Cube(metric, KindOf(metric), t0, codes, vocab, n, k, s, ss);
    }

    public bool[] Mask(IReadOnlyDictionary<string, string>? filters)
    {
        var m = Enumerable.Repeat(true, C).ToArray();
        if (filters is null) return m;
        foreach (var (dim, want) in filters)
        {
            var j = Dimensions.IndexOf(dim);
            var code = Vocab[dim].IndexOf(want);
            for (var c = 0; c < C; c++) m[c] &= code >= 0 && Codes[c][j] == code;
        }
        return m;
    }

    public Series Series(bool[]? mask = null)
    {
        var n = new double[T]; var k = new double[T]; var s = new double[T]; var ss = new double[T];
        for (var t = 0; t < T; t++)
            for (var c = 0; c < C; c++)
            {
                if (mask is not null && !mask[c]) continue;
                n[t] += N[t][c]; k[t] += K[t][c]; s[t] += S[t][c]; ss[t] += Ss[t][c];
            }
        return new Series(Kind, T0, n, k, s, ss);
    }

    /// <summary>Per-cell sums over [i0, i1).</summary>
    public (double[] N, double[] K, double[] S, double[] Ss) Window(int i0, int i1)
    {
        i0 = Math.Max(i0, 0); i1 = Math.Min(i1, T);
        if (i1 < i0) i1 = i0;
        double[] Diff(double[][] p) { var d = new double[C]; for (var c = 0; c < C; c++) d[c] = p[i1][c] - p[i0][c]; return d; }
        return (Diff(_pn), Diff(_pk), Diff(_ps), Diff(_pss));
    }
}
