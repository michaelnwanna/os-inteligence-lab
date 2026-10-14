using Ail.Schemas;

namespace Ail.Simulator;

/// <summary>Event-level expansion of the simulator, used to exercise the ingestion API end to end.</summary>
public static class EventStream
{
    private static readonly Dictionary<string, string> CpuByArch = new() { ["x64"] = "x64-8c", ["arm64"] = "arm64-8c", ["x86"] = "x86-4c" };

    /// <summary>One session event plus latency and memory samples per session, minute by minute.</summary>
    public static IEnumerable<TelemetryEvent> Events(FleetSimulator sim, int? minutes = null)
    {
        var sc = sim.Scenario;
        var rng = sim.Rng;
        var ncell = sim.CellCount;
        var last = Math.Min(sc.DurationMinutes, minutes ?? int.MaxValue);
        for (var minute = 0; minute < last; minute++)
        {
            var t = sim.TickAt(minute);
            var size = new int[ncell];
            foreach (var c in t.CellOfDevice) size[c]++;
            var offset = new int[ncell + 1];
            for (var c = 0; c < ncell; c++) offset[c + 1] = offset[c] + size[c];
            var fill = (int[])offset.Clone();
            var members = new int[t.CellOfDevice.Length];
            for (var d = 0; d < members.Length; d++) members[fill[t.CellOfDevice[d]]++] = d;

            for (var c = 0; c < ncell; c++)
            {
                var n = t.Sessions[c];
                if (n == 0) continue;
                var sigs = new List<string?>();
                foreach (var (name, arr) in t.SigCounts) for (var k = 0; k < arr[c]; k++) sigs.Add(name);
                sigs = sigs.Take(t.Failures[c]).ToList();
                var dims = sim.CellDims(c);
                var (latMean, latSd) = MeanSd(t.LatSum[c], t.LatSs[c], n, sc.Baseline.LatencyMs.Std);
                var (memMean, memSd) = MeanSd(t.MemSum[c], t.MemSs[c], n, sc.Baseline.MemoryMb.Std);
                for (var i = 0; i < n; i++)
                {
                    var device = members[offset[c] + (int)(rng.NextDouble() * size[c])];
                    var ts = DateTimeOffset.FromUnixTimeSeconds(t.Bucket * 60 + (long)(rng.NextDouble() * 60));
                    TelemetryEvent Make(string type) => new()
                    {
                        Timestamp = ts, DeviceId = $"dev-{device:D7}", SessionId = $"s-{t.Bucket}-{c}-{i}",
                        Os = sc.Population.Os, OsVersion = dims[0], Architecture = dims[1], DeviceModel = dims[2],
                        Cpu = CpuByArch.GetValueOrDefault(dims[1]), MemoryGb = dims[2].Length % 2 != 0 ? 8.0 : 16.0,
                        Application = dims[3], ApplicationVersion = dims[4], Driver = "gfx", DriverVersion = dims[5], EventType = type,
                    };
                    var session = Make("session");
                    session.FailureSignature = i < sigs.Count ? sigs[i] : null;
                    yield return session;
                    var lat = Make("metric"); lat.MetricName = "latency_ms"; lat.MetricValue = Math.Max(latMean + latSd * rng.Gaussian(), 1.0);
                    yield return lat;
                    var mem = Make("metric"); mem.MetricName = "memory_mb"; mem.MetricValue = Math.Max(memMean + memSd * rng.Gaussian(), 1.0);
                    yield return mem;
                }
            }
        }
    }

    // A one-session cell has no sample variance, so fall back to the scenario's spread.
    private static (double Mean, double Sd) MeanSd(double sum, double ss, int n, double fallbackSd)
    {
        var mean = sum / n;
        if (n < 2) return (mean, fallbackSd);
        return (mean, Math.Sqrt(Math.Max((ss - n * mean * mean) / (n - 1), 0.0)));
    }
}
