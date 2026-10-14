using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Ail.Schemas;

namespace Ail.Ingestion;

public sealed record Normalized(string EventId, long Ts, long Bucket, string[] Dims, string Kind, bool Failed,
    string? Signature, string? MetricName, double? MetricValue, string Payload);

/// <summary>Validation follow-up: normalization, enrichment and aggregation of telemetry events.</summary>
public static class Processing
{
    private static readonly Dictionary<string, string> ArchAliases = new()
    {
        ["x86_64"] = "x64", ["amd64"] = "x64", ["x64"] = "x64", ["aarch64"] = "arm64", ["arm64"] = "arm64",
        ["i386"] = "x86", ["i686"] = "x86", ["x86"] = "x86",
    };

    public static Normalized Normalize(TelemetryEvent ev)
    {
        var raw = ev.Architecture!.Trim().ToLowerInvariant();
        var arch = ArchAliases.GetValueOrDefault(raw, raw);
        var dims = new[]
        {
            ev.OsVersion!.Trim(), arch, ev.DeviceModel!.Trim(), ev.Application!.Trim(), ev.ApplicationVersion!.Trim(),
            (ev.DriverVersion ?? "none").Trim(),
        };
        var kind = ev.EventType == "metric" ? "metric" : "session";
        var signature = ev.FailureSignature;
        if (ev.EventType == "crash" && string.IsNullOrEmpty(signature)) signature = "UNKNOWN_CRASH";
        // Enrichment: a stable cohort tag makes raw events searchable by cohort.
        var enriched = new Dictionary<string, string>(ev.Dimensions) { ["cohort"] = string.Join("|", dims) };
        var copy = JsonSerializer.Deserialize<TelemetryEvent>(JsonSerializer.Serialize(ev, AilJson.Options), AilJson.Options)!;
        copy.Architecture = arch;
        copy.Dimensions = enriched;
        return new Normalized(ev.ResolvedEventId(), ev.Timestamp.ToUnixTimeSeconds(), TimeUtil.BucketOf(ev.Timestamp), dims, kind,
            signature is not null, signature, ev.MetricName, ev.MetricValue, JsonSerializer.Serialize(copy, AilJson.Options));
    }

    /// <summary>Roll a batch up into minute/cohort aggregate rows.</summary>
    public static List<AggRow> AggregateRows(IEnumerable<Normalized> batch)
    {
        var acc = new Dictionary<string, (long Bucket, string Metric, string[] Dims, double[] V)>();
        void Add(long bucket, string metric, string[] dims, double n, double k, double s, double ss)
        {
            var key = bucket + "|" + metric + "|" + string.Join("|", dims);
            if (!acc.TryGetValue(key, out var cell)) acc[key] = cell = (bucket, metric, dims, new double[4]);
            cell.V[0] += n; cell.V[1] += k; cell.V[2] += s; cell.V[3] += ss;
        }
        foreach (var e in batch)
        {
            if (e.Kind == "session")
            {
                Add(e.Bucket, "failure_rate", e.Dims, 1, e.Failed ? 1 : 0, 0, 0);
                if (e.Signature is not null) Add(e.Bucket, "sig:" + e.Signature, e.Dims, 1, 0, 0, 0);
            }
            else
            {
                var v = e.MetricValue ?? 0;
                Add(e.Bucket, e.MetricName!, e.Dims, 1, 0, v, v * v);
            }
        }
        return acc.Values.Select(c => new AggRow(c.Bucket, c.Metric, c.Dims, c.V[0], c.V[1], c.V[2], c.V[3])).ToList();
    }
}

public sealed class BackpressureException : Exception
{
    public BackpressureException() : base("event stream is full") { }
}

/// <summary>Streaming abstraction; the in-memory implementation can be replaced by Redis Streams or Kafka.</summary>
public sealed class InMemoryStream
{
    private readonly ConcurrentQueue<TelemetryEvent> _q = new();
    private readonly object _lock = new();
    public int MaxSize { get; }

    public InMemoryStream(int maxSize = 200_000) => MaxSize = maxSize;

    public void Publish(IReadOnlyList<TelemetryEvent> events)
    {
        lock (_lock)
        {
            if (_q.Count + events.Count > MaxSize) throw new BackpressureException();
            foreach (var e in events) _q.Enqueue(e);
        }
    }

    public List<TelemetryEvent> Poll(int max)
    {
        var batch = new List<TelemetryEvent>();
        while (batch.Count < max && _q.TryDequeue(out var e)) batch.Add(e);
        return batch;
    }

    public int Depth => _q.Count;
}

/// <summary>normalize, de-duplicate, store raw, enrich, aggregate.</summary>
public sealed class IngestPipeline
{
    private readonly TelemetryStore _store;
    public IngestPipeline(TelemetryStore store) => _store = store;

    public int Process(IReadOnlyList<TelemetryEvent> batch)
    {
        var start = Stopwatch.GetTimestamp();
        var normalized = batch.Select(Processing.Normalize).ToList();
        var fresh = _store.InsertRaw(normalized.Select(n => (n.EventId, n.Ts, n.Payload)).ToList());
        var added = normalized.Where((_, i) => fresh[i]).ToList();
        _store.AddAggregates(Processing.AggregateRows(added));
        MetricsRegistry.Instance.Inc("ail_events_stored_total", added.Count);
        MetricsRegistry.Instance.Inc("ail_events_duplicate_total", batch.Count - added.Count);
        MetricsRegistry.Instance.Observe("ail_processing_latency_seconds", Stopwatch.GetElapsedTime(start).TotalSeconds);
        return added.Count;
    }
}

public sealed class StreamProcessor : IDisposable
{
    private readonly InMemoryStream _stream;
    private readonly IngestPipeline _pipeline;
    private readonly CancellationTokenSource _cts = new();
    private Task? _worker;
    private readonly int _batchSize;

    public StreamProcessor(InMemoryStream stream, IngestPipeline pipeline, int batchSize = 2000)
    {
        _stream = stream; _pipeline = pipeline; _batchSize = batchSize;
    }

    /// <summary>Process everything currently queued, synchronously.</summary>
    public int Drain()
    {
        var total = 0;
        while (true)
        {
            var batch = _stream.Poll(_batchSize);
            if (batch.Count == 0) return total;
            try { total += _pipeline.Process(batch); }
            catch (Exception)
            {
                MetricsRegistry.Instance.Inc("ail_processing_errors_total", batch.Count);
            }
        }
    }

    public void Start()
    {
        if (_worker is not null) return;
        _worker = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
                if (Drain() == 0) await Task.Delay(50, CancellationToken.None);
        });
    }

    public void Stop()
    {
        _cts.Cancel();
        _worker?.Wait(TimeSpan.FromSeconds(5));
        Drain();
    }

    public void Dispose() => Stop();
}
