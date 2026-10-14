using Ail.Schemas;

namespace Ail.Simulator;

/// <summary>Per-minute simulation output, one value per cohort cell.</summary>
public sealed class Tick
{
    public int Minute { get; init; }
    public long Bucket { get; init; }
    public int[] CellOfDevice { get; init; } = Array.Empty<int>();
    public int[] Sessions { get; init; } = Array.Empty<int>();
    public int[] Failures { get; init; } = Array.Empty<int>();
    public double[] LatSum { get; init; } = Array.Empty<double>();
    public double[] LatSs { get; init; } = Array.Empty<double>();
    public double[] MemSum { get; init; } = Array.Empty<double>();
    public double[] MemSs { get; init; } = Array.Empty<double>();
    public Dictionary<string, int[]> SigCounts { get; init; } = new();
}

/// <summary>
/// Reproducible synthetic population. Devices get attributes once (seeded); each simulated minute rollouts
/// move devices to new versions, devices complete sessions, and injected effects change the failure rate,
/// latency and memory of matching cohorts.
/// </summary>
public sealed class FleetSimulator
{
    private readonly Rng _rng;
    private readonly int _n;
    private readonly string[] _osNames, _appNames, _drvNames, _modelNames, _modelArch;
    private readonly int[] _model, _os0, _app0, _drv0;
    private readonly List<Rollout> _rollouts;
    private readonly double[][] _rolloutU;
    private readonly int[] _shape;
    private readonly int _ncell;
    private readonly int[] _cellOs, _cellModel, _cellApp, _cellDrv;
    private readonly double[] _modelFail, _modelLat, _modelMem;
    private readonly Dictionary<string, string[]> _cellValues = new();
    private readonly Dictionary<string, bool[]> _effectMasks = new();
    private readonly string[][] _cellDims;
    private readonly (string[] Names, double[] Probs) _baseSigs;

    public Scenario Scenario { get; }
    public int Devices => _n;
    public int CellCount => _ncell;
    public Rng Rng => _rng;

    public FleetSimulator(Scenario scenario, int? devices = null, int? seed = null)
    {
        Scenario = scenario;
        var pop = scenario.Population;
        _n = devices ?? pop.Devices;
        _rng = new Rng(seed ?? scenario.Seed);

        var archNames = pop.Architectures.Keys.ToArray();
        var archP = Normalize(archNames.Select(a => pop.Architectures[a]).ToArray());
        var models = new List<string>();
        var arch = new List<string>();
        var offsets = new List<int>();
        var counts = new List<int>();
        foreach (var a in archNames)
        {
            offsets.Add(models.Count);
            counts.Add(pop.Models[a].Count);
            foreach (var m in pop.Models[a]) { models.Add(m); arch.Add(a); }
        }
        _modelNames = models.ToArray();
        _modelArch = arch.ToArray();

        string[] Vocab(Dictionary<string, double> baseDist, string dim)
        {
            var names = baseDist.Keys.ToList();
            foreach (var r in scenario.Rollouts)
                if (r.Dimension == dim && !names.Contains(r.ToVersion)) names.Add(r.ToVersion);
            return names.ToArray();
        }
        _osNames = Vocab(pop.OsVersions, "os_version");
        _appNames = Vocab(pop.AppVersions, "application_version");
        _drvNames = Vocab(pop.Drivers, "driver_version");

        int[] Draw(Dictionary<string, double> dist, string[] names)
        {
            var cum = Cumulative(Normalize(names.Select(n => dist.GetValueOrDefault(n, 0.0)).ToArray()));
            var result = new int[_n];
            for (var i = 0; i < _n; i++) result[i] = _rng.Choice(cum);
            return result;
        }

        var archCum = Cumulative(archP);
        var archIdx = new int[_n];
        for (var i = 0; i < _n; i++) archIdx[i] = _rng.Choice(archCum);
        _model = new int[_n];
        for (var i = 0; i < _n; i++)
            _model[i] = offsets[archIdx[i]] + (int)(_rng.NextDouble() * counts[archIdx[i]]);
        _os0 = Draw(pop.OsVersions, _osNames);
        _app0 = Draw(pop.AppVersions, _appNames);
        _drv0 = Draw(pop.Drivers, _drvNames);
        _rollouts = scenario.Rollouts.OrderBy(r => r.AtMinute).ToList();
        _rolloutU = _rollouts.Select(_ => Enumerable.Range(0, _n).Select(__ => _rng.NextDouble()).ToArray()).ToArray();

        var m0 = _modelNames.Length;
        _shape = new[] { _osNames.Length, m0, _appNames.Length, _drvNames.Length };
        _ncell = _shape[0] * _shape[1] * _shape[2] * _shape[3];
        _cellOs = new int[_ncell]; _cellModel = new int[_ncell]; _cellApp = new int[_ncell]; _cellDrv = new int[_ncell];
        for (var c = 0; c < _ncell; c++)
        {
            var rem = c;
            _cellDrv[c] = rem % _shape[3]; rem /= _shape[3];
            _cellApp[c] = rem % _shape[2]; rem /= _shape[2];
            _cellModel[c] = rem % _shape[1]; rem /= _shape[1];
            _cellOs[c] = rem;
        }
        var spread = scenario.Baseline.ModelSpread;
        _modelFail = _rng.LognormalFactors(m0, spread);
        _modelLat = _rng.LognormalFactors(m0, spread / 2);
        _modelMem = _rng.LognormalFactors(m0, spread / 2);

        _cellValues["os_version"] = _cellOs.Select(i => _osNames[i]).ToArray();
        _cellValues["architecture"] = _cellModel.Select(i => _modelArch[i]).ToArray();
        _cellValues["device_model"] = _cellModel.Select(i => _modelNames[i]).ToArray();
        _cellValues["application"] = Enumerable.Repeat(pop.Application, _ncell).ToArray();
        _cellValues["application_version"] = _cellApp.Select(i => _appNames[i]).ToArray();
        _cellValues["driver_version"] = _cellDrv.Select(i => _drvNames[i]).ToArray();
        _cellDims = Enumerable.Range(0, _ncell).Select(c => Dimensions.All.Select(d => _cellValues[d][c]).ToArray()).ToArray();

        foreach (var fx in scenario.Effects) _effectMasks[fx.Id] = Mask(fx);
        var sigNames = scenario.Baseline.Signatures.Keys.ToArray();
        _baseSigs = (sigNames, Normalize(sigNames.Select(s => scenario.Baseline.Signatures[s]).ToArray()));
    }

    private static double[] Normalize(double[] w) { var s = w.Sum(); return w.Select(x => x / s).ToArray(); }

    private static double[] Cumulative(double[] p)
    {
        var c = new double[p.Length];
        double acc = 0;
        for (var i = 0; i < p.Length; i++) { acc += p[i]; c[i] = acc; }
        return c;
    }

    private bool[] Mask(Effect fx)
    {
        var mask = Enumerable.Repeat(true, _ncell).ToArray();
        foreach (var (dim, want) in fx.Where)
        {
            if (!_cellValues.TryGetValue(dim, out var values)) throw new ArgumentException($"effect {fx.Id}: unknown dimension '{dim}'");
            var wanted = want is string s ? new HashSet<string> { s } : ((IEnumerable<object>)want).Select(o => o.ToString()!).ToHashSet();
            for (var c = 0; c < _ncell; c++) mask[c] &= wanted.Contains(values[c]);
        }
        return mask;
    }

    public static double Exposure(Rollout r, int minute)
    {
        if (minute < r.AtMinute) return 0;
        if (r.RampMinutes <= 0) return r.MaxExposure;
        return r.MaxExposure * Math.Min(1.0, (minute - r.AtMinute) / (double)r.RampMinutes);
    }

    private static double Alpha(Effect fx, int minute)
    {
        if (minute < fx.StartMinute || (fx.EndMinute is { } end && minute >= end)) return 0;
        if (fx.RampMinutes <= 0) return 1;
        return Math.Min(1.0, (minute - fx.StartMinute + 1) / (double)fx.RampMinutes);
    }

    public string[] CellDims(int cell) => _cellDims[cell];

    /// <summary>Cell index of every device at this minute, after applying rollouts.</summary>
    public int[] DeviceCells(int minute)
    {
        var os = (int[])_os0.Clone();
        var app = (int[])_app0.Clone();
        var drv = (int[])_drv0.Clone();
        for (var ri = 0; ri < _rollouts.Count; ri++)
        {
            var r = _rollouts[ri];
            var e = Exposure(r, minute);
            if (e <= 0) continue;
            var (arr, names) = r.Dimension switch
            {
                "os_version" => (os, _osNames),
                "application_version" => (app, _appNames),
                _ => (drv, _drvNames),
            };
            var from = Array.IndexOf(names, r.FromVersion);
            var to = Array.IndexOf(names, r.ToVersion);
            var u = _rolloutU[ri];
            for (var i = 0; i < _n; i++) if (arr[i] == from && u[i] < e) arr[i] = to;
        }
        var cells = new int[_n];
        for (var i = 0; i < _n; i++) cells[i] = ((os[i] * _shape[1] + _model[i]) * _shape[2] + app[i]) * _shape[3] + drv[i];
        return cells;
    }

    private (double[] Sum, double[] Ss) Gauge(int[] n, double[] mean, double[] sd)
    {
        var sum = new double[_ncell];
        var ss = new double[_ncell];
        for (var c = 0; c < _ncell; c++)
        {
            var n1 = Math.Max(n[c], 1);
            var meanObs = mean[c] + sd[c] / Math.Sqrt(n1) * _rng.Gaussian();
            var dof = Math.Max(n[c] - 1, 1);
            var varObs = sd[c] * sd[c] * _rng.ChiSquare(dof) / dof;
            sum[c] = n[c] * meanObs;
            ss[c] = Math.Max(n[c] - 1, 0) * varObs + n[c] * meanObs * meanObs;
        }
        return (sum, ss);
    }

    public Tick TickAt(int minute)
    {
        var sc = Scenario;
        var b = sc.Baseline;
        var cellOfDevice = DeviceCells(minute);
        var devices = new int[_ncell];
        foreach (var c in cellOfDevice) devices[c]++;

        var volume = 1.0;
        foreach (var tr in sc.Traffic) if (tr.StartMinute <= minute && minute < tr.EndMinute) volume *= tr.Multiplier;
        var pActive = Math.Min(1.0, b.ActiveSessionRate * volume);
        var sessions = new int[_ncell];
        for (var c = 0; c < _ncell; c++) sessions[c] = _rng.Binomial(devices[c], pActive);

        var rate = new double[_ncell];
        var latMean = new double[_ncell]; var memMean = new double[_ncell];
        var latScale = new double[_ncell]; var memScale = new double[_ncell];
        for (var c = 0; c < _ncell; c++)
        {
            rate[c] = b.FailureRate * _modelFail[_cellModel[c]];
            latMean[c] = b.LatencyMs.Mean * _modelLat[_cellModel[c]];
            memMean[c] = b.MemoryMb.Mean * _modelMem[_cellModel[c]];
            latScale[c] = 1; memScale[c] = 1;
        }
        var excess = new List<(Effect Fx, double[] Ex)>();
        foreach (var fx in sc.Effects)
        {
            var a = Alpha(fx, minute);
            if (a <= 0) continue;
            var mask = _effectMasks[fx.Id];
            if (fx.FailureRate is { } target)
            {
                // the target is for an average device; per-model differences are preserved
                var factor = 1 + (target / b.FailureRate - 1) * a;
                var ex = new double[_ncell];
                for (var c = 0; c < _ncell; c++)
                    if (mask[c]) { var next = rate[c] * factor; ex[c] = Math.Max(next - rate[c], 0); rate[c] = next; }
                excess.Add((fx, ex));
            }
            for (var c = 0; c < _ncell; c++)
            {
                if (!mask[c]) continue;
                latScale[c] *= 1 + (fx.LatencyMultiplier - 1) * a;
                memScale[c] *= 1 + (fx.MemoryMultiplier - 1) * a;
            }
        }

        var failures = new int[_ncell];
        for (var c = 0; c < _ncell; c++) failures[c] = _rng.Binomial(sessions[c], Math.Clamp(rate[c], 0, 1));

        var remaining = (int[])failures.Clone();
        var remProb = Enumerable.Repeat(1.0, _ncell).ToArray();
        var sigCounts = new Dictionary<string, int[]>();
        int[] Slot(string name) => sigCounts.TryGetValue(name, out var arr) ? arr : sigCounts[name] = new int[_ncell];
        foreach (var (fx, ex) in excess)
        {
            if (string.IsNullOrEmpty(fx.Signature)) continue;
            var slot = Slot(fx.Signature);
            for (var c = 0; c < _ncell; c++)
            {
                var share = ex[c] / Math.Max(rate[c], 1e-12);
                var take = _rng.Binomial(remaining[c], Math.Clamp(share / Math.Max(remProb[c], 1e-12), 0, 1));
                remaining[c] -= take;
                remProb[c] -= share;
                slot[c] += take;
            }
        }
        var left = 1.0;
        for (var i = 0; i < _baseSigs.Names.Length; i++)
        {
            var slot = Slot(_baseSigs.Names[i]);
            var last = i == _baseSigs.Names.Length - 1;
            for (var c = 0; c < _ncell; c++)
            {
                var take = last ? remaining[c] : _rng.Binomial(remaining[c], Math.Min(1.0, _baseSigs.Probs[i] / left));
                remaining[c] -= take;
                slot[c] += take;
            }
            left -= _baseSigs.Probs[i];
        }

        var latSd = latScale.Select(s => b.LatencyMs.Std * s).ToArray();
        var memSd = memScale.Select(s => b.MemoryMb.Std * s).ToArray();
        var lat = Gauge(sessions, latMean.Select((m, c) => m * latScale[c]).ToArray(), latSd);
        var mem = Gauge(sessions, memMean.Select((m, c) => m * memScale[c]).ToArray(), memSd);
        return new Tick
        {
            Minute = minute, Bucket = Schemas.TimeUtil.BucketOf(sc.Start) + minute, CellOfDevice = cellOfDevice,
            Sessions = sessions, Failures = failures, LatSum = lat.Sum, LatSs = lat.Ss, MemSum = mem.Sum, MemSs = mem.Ss,
            SigCounts = sigCounts,
        };
    }

    /// <summary>Aggregate rows for one tick.</summary>
    public IEnumerable<AggRow> Rows(Tick t)
    {
        for (var c = 0; c < _ncell; c++)
        {
            if (t.Sessions[c] == 0) continue;
            var d = _cellDims[c];
            double n = t.Sessions[c];
            yield return new AggRow(t.Bucket, "failure_rate", d, n, t.Failures[c], 0, 0);
            yield return new AggRow(t.Bucket, "latency_ms", d, n, 0, t.LatSum[c], t.LatSs[c]);
            yield return new AggRow(t.Bucket, "memory_mb", d, n, 0, t.MemSum[c], t.MemSs[c]);
            foreach (var (name, arr) in t.SigCounts)
                if (arr[c] > 0) yield return new AggRow(t.Bucket, "sig:" + name, d, arr[c], 0, 0, 0);
        }
    }

    public List<ChangeEvent> ChangeEvents()
    {
        var sc = Scenario;
        var extra = sc.Changes.Select(c => new ChangeEvent
        {
            DeploymentId = c.Id, Timestamp = sc.Start.AddMinutes(c.AtMinute), Kind = c.Kind, Component = c.Component,
            Description = string.IsNullOrEmpty(c.Description) ? c.Component : c.Description,
        });
        var rolled = _rollouts.Select(r => new ChangeEvent
        {
            DeploymentId = r.Id, Timestamp = sc.Start.AddMinutes(r.AtMinute), Kind = r.Kind, Component = r.Component,
            Dimension = r.Dimension, FromVersion = r.FromVersion, ToVersion = r.ToVersion,
            Description = string.IsNullOrEmpty(r.Description) ? $"{r.Component} {r.FromVersion} -> {r.ToVersion}" : r.Description,
            Repository = r.Repository, BaseRef = r.BaseRef, HeadRef = r.HeadRef,
            Metadata = new() { ["ramp_minutes"] = r.RampMinutes, ["max_exposure"] = r.MaxExposure },
        });
        return extra.Concat(rolled).ToList();
    }

    /// <summary>Fast path: write minute/cohort aggregates straight into a sink.</summary>
    public static FleetSimulator SimulateToStore(Scenario scenario, IAggregateSink sink, int? devices = null, int? seed = null,
        Action<int, int>? progress = null)
    {
        var sim = new FleetSimulator(scenario, devices, seed);
        foreach (var change in sim.ChangeEvents()) sink.AddChange(change);
        for (var minute = 0; minute < scenario.DurationMinutes; minute++)
        {
            sink.AddAggregates(sim.Rows(sim.TickAt(minute)));
            progress?.Invoke(minute + 1, scenario.DurationMinutes);
        }
        sink.Commit();
        sink.SetMeta("simulation", new Dictionary<string, object?>
        {
            ["scenario"] = scenario.Id, ["name"] = scenario.Name, ["devices"] = sim._n, ["seed"] = seed ?? scenario.Seed,
            ["config_hash"] = scenario.ConfigHash(), ["start"] = scenario.Start.ToString("O"),
            ["duration_minutes"] = scenario.DurationMinutes, ["application"] = scenario.Population.Application,
        });
        return sim;
    }
}
