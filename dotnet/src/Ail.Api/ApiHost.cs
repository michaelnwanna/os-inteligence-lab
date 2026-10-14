using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ail.Detection;
using Ail.Ingestion;
using Ail.Investigation;
using Ail.Schemas;
using Ail.Simulator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ail.Api;

public sealed class ApiOptions
{
    public string DbPath { get; set; } = "data/ail.db";
    /// <summary>Process ingested events synchronously (tests); otherwise a worker drains the stream.</summary>
    public bool Inline { get; set; }
    public string? ApiToken { get; set; } = Environment.GetEnvironmentVariable("AIL_API_TOKEN");
    public string? ReadToken { get; set; } = Environment.GetEnvironmentVariable("AIL_READ_TOKEN");
    public double? RateLimit { get; set; } = double.TryParse(Environment.GetEnvironmentVariable("AIL_RATE_LIMIT"), out var r) ? r : null;
    public string[] Urls { get; set; } = Array.Empty<string>();
}

/// <summary>Per-client token bucket for write endpoints.</summary>
public sealed class RateLimiter
{
    private readonly double _rate, _burst;
    private readonly Dictionary<string, (double Tokens, long Last)> _buckets = new();
    private readonly object _lock = new();

    public RateLimiter(double rate) { _rate = rate; _burst = Math.Max(rate * 2, 1.0); }

    public (bool Allowed, double Wait) Allow(string key, long? nowTicks = null)
    {
        var now = nowTicks ?? System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_lock)
        {
            var (tokens, last) = _buckets.GetValueOrDefault(key, (_burst, now));
            tokens = Math.Min(_burst, tokens + (now - last) / (double)System.Diagnostics.Stopwatch.Frequency * _rate);
            var allowed = tokens >= 1.0;
            _buckets[key] = (allowed ? tokens - 1.0 : tokens, now);
            return (allowed, allowed ? 0 : (1.0 - tokens) / _rate);
        }
    }
}

/// <summary>HTTP API plus the static portal: the same endpoints and wire format as the Python implementation.</summary>
public static class ApiHost
{
    private const int MaxBodyBytes = 10 * 1024 * 1024;

    private static IResult Detail(int status, string message) =>
        Results.Json(new Dictionary<string, object> { ["detail"] = message }, AilJson.Options, statusCode: status);

    private static bool TokenOk(HttpContext ctx, string? token)
    {
        if (token is null) return true;
        var supplied = ctx.Request.Headers.Authorization.ToString();
        supplied = (supplied.StartsWith("Bearer ") ? supplied[7..] : supplied).Trim();
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(token));
    }

    private static Dictionary<string, string> ParseFilters(string? raw)
    {
        var filters = new Dictionary<string, string>();
        foreach (var part in (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2 || Dimensions.IndexOf(kv[0]) < 0 || kv[1].Length == 0)
                throw new ArgumentException($"invalid filter '{part}'; keys must be one of [{string.Join(", ", Dimensions.All)}]");
            filters[kv[0]] = kv[1];
        }
        return filters;
    }

    private static Dictionary<string, object?> Summary(Incident inc)
    {
        var dims = inc.Cohorts.FirstOrDefault()?.Dimensions ?? new Dictionary<string, string>();
        var trig = inc.Correlations.Count > 0 && !inc.Correlations[0].AfterOnset ? inc.Correlations[0] : null;
        return new()
        {
            ["id"] = inc.Id, ["title"] = inc.Title, ["status"] = inc.Status, ["severity"] = inc.Severity, ["metric"] = inc.Metric,
            ["onset"] = inc.Onset, ["first_detection"] = inc.FirstDetection, ["baseline"] = inc.Baseline, ["observed"] = inc.Observed,
            ["cohort"] = Labels.CohortLabel(dims), ["cohort_dimensions"] = dims,
            ["trigger"] = trig is null ? null : $"{trig.Change.Component} {trig.Change.ToVersion}".Trim(), ["correlation"] = trig?.Score,
        };
    }

    private sealed class State
    {
        private readonly TelemetryStore _store;
        private Workspace? _ws;
        private (long, (long, long)?)? _key;
        private readonly object _lock = new();
        public State(TelemetryStore store) => _store = store;

        public Workspace Ws()
        {
            var key = (_store.Counts()["aggregate_rows"], _store.TimeRange());
            lock (_lock)
            {
                if (_ws is null || _key != key)
                {
                    _ws = new Workspace(_store, new DetectionConfig());
                    _key = key;
                }
                return _ws;
            }
        }
    }

    public static WebApplication Create(ApiOptions options)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = Directory.GetCurrentDirectory() });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        if (options.Urls.Length > 0) builder.WebHost.UseUrls(options.Urls);
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            o.SerializerOptions.NumberHandling = AilJson.Options.NumberHandling;
        });
        var app = builder.Build();

        var store = new TelemetryStore(options.DbPath);
        var stream = new InMemoryStream();
        var processor = new StreamProcessor(stream, new IngestPipeline(store));
        var limiter = options.RateLimit is > 0 ? new RateLimiter(options.RateLimit.Value) : null;
        var state = new State(store);
        var reg = MetricsRegistry.Instance;
        reg.Gauge("ail_queue_depth", () => stream.Depth);
        app.Lifetime.ApplicationStarted.Register(() => { if (!options.Inline) processor.Start(); });
        app.Lifetime.ApplicationStopping.Register(processor.Stop);
        app.Lifetime.ApplicationStopped.Register(store.Dispose);

        app.Use(async (ctx, next) =>
        {
            var guarded = ctx.Request.Path.StartsWithSegments("/api/v1") || ctx.Request.Path == "/metrics";
            if (options.ReadToken is not null && ctx.Request.Method == "GET" && guarded && !TokenOk(ctx, options.ReadToken))
            {
                await Detail(401, "missing or invalid read token").ExecuteAsync(ctx);
                return;
            }
            try { await next(ctx); }
            catch (ArgumentException ex) { await Detail(400, ex.Message).ExecuteAsync(ctx); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("no telemetry")) { await Detail(409, "no telemetry has been ingested yet").ExecuteAsync(ctx); }
        });

        // ---- ingestion ---------------------------------------------------------------------
        async Task<IResult> Ingest(HttpContext ctx, string key, string? forceType)
        {
            if (!TokenOk(ctx, options.ApiToken)) return Detail(401, "missing or invalid API token");
            if (limiter is not null)
            {
                var (ok, wait) = limiter.Allow(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown");
                if (!ok)
                {
                    reg.Inc("ail_ingest_throttled_total");
                    ctx.Response.Headers["Retry-After"] = Math.Max(1, (int)Math.Ceiling(wait)).ToString();
                    return Detail(429, "rate limit exceeded");
                }
            }
            if (ctx.Request.ContentLength > MaxBodyBytes) return Detail(413, "request body too large");
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms);
            if (ms.Length > MaxBodyBytes) return Detail(413, "request body too large");
            JsonElement root;
            try { root = JsonDocument.Parse(ms.ToArray()).RootElement; }
            catch (JsonException) { return Detail(400, "invalid JSON"); }
            List<JsonElement> items;
            if (root.ValueKind == JsonValueKind.Array) items = root.EnumerateArray().ToList();
            else if (root.ValueKind == JsonValueKind.Object)
                items = root.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().ToList() : new() { root };
            else return Detail(400, "body must be a JSON object or array");

            if (key == "deployments")
            {
                int accepted = 0; var rejectedD = new List<object>();
                for (var i = 0; i < items.Count; i++)
                {
                    try
                    {
                        var ch = JsonSerializer.Deserialize<ChangeEvent>(items[i].GetRawText(), AilJson.Strict)!;
                        var errs = ch.Validate();
                        if (errs.Count > 0) { rejectedD.Add(new { index = i, error = errs[0] }); continue; }
                        store.AddChange(ch); accepted++;
                    }
                    catch (JsonException ex) { rejectedD.Add(new { index = i, error = ex.Message }); }
                }
                return Results.Json(new { accepted, rejected = rejectedD }, AilJson.Options, statusCode: 202);
            }

            var valid = new List<TelemetryEvent>(); var rejected = new List<object>();
            for (var i = 0; i < items.Count; i++)
            {
                try
                {
                    var ev = JsonSerializer.Deserialize<TelemetryEvent>(items[i].GetRawText(), AilJson.Strict)!;
                    var errs = ev.Validate().ToList();
                    if (forceType is not null && ev.EventType != forceType) errs.Add($"event_type must be '{forceType}' on this endpoint");
                    if (errs.Count > 0) rejected.Add(new { index = i, error = errs[0] }); else valid.Add(ev);
                }
                catch (JsonException ex) { rejected.Add(new { index = i, error = ex.Message }); }
            }
            try { stream.Publish(valid); }
            catch (BackpressureException)
            {
                ctx.Response.Headers["Retry-After"] = "1";
                return Detail(503, "stream full, retry later");
            }
            if (options.Inline) processor.Drain();
            reg.Inc("ail_ingest_events_total", valid.Count);
            reg.Inc("ail_ingest_rejected_total", rejected.Count);
            return Results.Json(new { accepted = valid.Count, rejected }, AilJson.Options, statusCode: 202);
        }

        app.MapPost("/api/v1/events", async (HttpContext c) => await (await Ingest(c, "events", null)).ExecuteAsync(c));
        app.MapPost("/api/v1/metrics", async (HttpContext c) => await (await Ingest(c, "metrics", "metric")).ExecuteAsync(c));
        app.MapPost("/api/v1/deployments", async (HttpContext c) => await (await Ingest(c, "deployments", null)).ExecuteAsync(c));
        app.MapGet("/health", () => Results.Json(new Dictionary<string, object> { ["status"] = "ok", ["queue_depth"] = stream.Depth }, AilJson.Options));
        app.MapGet("/metrics", () => Results.Text(reg.Render(), "text/plain"));

        // ---- queries -----------------------------------------------------------------------
        Dictionary<string, object?> Meta()
        {
            var range = store.TimeRange();
            string provider; try { provider = LlmProviders.Create().Name; } catch (Exception) { provider = "unavailable"; }
            return new()
            {
                ["simulation"] = store.GetMeta<JsonElement?>("simulation"), ["counts"] = store.Counts(),
                ["time_range"] = range is { } r ? new[] { TimeUtil.Iso(r.Lo), TimeUtil.Iso(r.Hi) } : null, ["llm_provider"] = provider,
            };
        }

        app.MapGet("/api/v1/meta", () => Results.Json(Meta(), AilJson.Options));
        app.MapGet("/api/v1/overview", () =>
        {
            var incidents = store.ListIncidents();
            var active = incidents.Where(i => i.Status != "resolved").ToList();
            var o = new Dictionary<string, object?>
            {
                ["meta"] = Meta(),
                ["incidents"] = new Dictionary<string, object?>
                {
                    ["total"] = incidents.Count, ["active"] = active.Count, ["critical"] = active.Count(i => i.Severity == "critical"),
                    ["investigating"] = incidents.Count(i => i.Status == "investigating"), ["resolved"] = incidents.Count(i => i.Status == "resolved"),
                },
                ["recent"] = incidents.Take(5).Select(Summary).ToList(),
            };
            if (store.TimeRange() is not null)
            {
                var ws = state.Ws();
                o["health"] = Explore.Health(ws);
                var ev = ws.Scanner("failure_rate").Evaluate(ws.Cube("failure_rate").Mask(null), ws.T - 10, ws.T);
                o["failure_rate"] = new Dictionary<string, object?> { ["baseline"] = ev.Baseline, ["current"] = ev.Observed, ["ratio"] = ev.Ratio };
            }
            return Results.Json(o, AilJson.Options);
        });
        app.MapGet("/api/v1/incidents", () => Results.Json(store.ListIncidents().Select(Summary).ToList(), AilJson.Options));
        app.MapGet("/api/v1/incidents/{id}", (string id) =>
        {
            var inc = store.GetIncident(id);
            if (inc is null) return Detail(404, "incident not found");
            var node = JsonSerializer.SerializeToNode(inc, AilJson.Options)!.AsObject();
            node.Remove("timemachine");
            node["cohort_label"] = Labels.CohortLabel(inc.Cohorts.FirstOrDefault()?.Dimensions);
            return Results.Text(node.ToJsonString(AilJson.Options), "application/json");
        });
        app.MapGet("/api/v1/incidents/{id}/timemachine", (string id) =>
        {
            var inc = store.GetIncident(id);
            return inc?.Timemachine is null ? Detail(404, "no time machine data for this incident") : Results.Json(inc.Timemachine, AilJson.Options);
        });
        app.MapGet("/api/v1/incidents/{id}/report", (string id) =>
            store.GetIncident(id) is { } inc ? Results.Text(Report.RenderMarkdown(inc), "text/plain") : Detail(404, "incident not found"));

        app.MapGet("/api/v1/anomalies", (HttpContext c) =>
        {
            var q = c.Request.Query;
            string? Q(string k) => q.TryGetValue(k, out var v) && v.Count > 0 && v[0]!.Length > 0 ? v[0] : null;
            var wanted = new[] { "os_version", "architecture", "device_model", "application_version", "driver_version" }
                .Where(k => Q(k) is not null).ToDictionary(k => k, k => Q(k)!);
            var rows = new List<Dictionary<string, object?>>();
            foreach (var inc in store.ListIncidents())
            {
                var cohort = inc.Cohorts.FirstOrDefault()?.Dimensions ?? new();
                foreach (var r in inc.Results)
                {
                    var dims = new Dictionary<string, string>(cohort);
                    foreach (var (k, v) in r.Dimensions) dims[k] = v;
                    if ((Q("metric") is { } m && r.Metric != m) || (Q("severity") is { } s && r.Severity != s) || (Q("detector") is { } d && r.Detector != d)) continue;
                    if (wanted.Any(w => dims.GetValueOrDefault(w.Key) != w.Value)) continue;
                    rows.Add(new()
                    {
                        ["incident_id"] = inc.Id, ["scope"] = Labels.CohortLabel(dims), ["dimensions"] = dims, ["metric"] = r.Metric, ["timestamp"] = r.Timestamp,
                        ["onset"] = r.Onset, ["baseline"] = r.Baseline, ["observed_value"] = r.ObservedValue, ["delta"] = r.Delta, ["severity"] = r.Severity,
                        ["severity_score"] = r.SeverityScore, ["confidence"] = r.Confidence, ["affected_population"] = r.AffectedPopulation, ["detector"] = r.Detector,
                        ["evidence"] = r.Evidence,
                    });
                }
            }
            return Results.Json(rows.OrderBy(r => (DateTimeOffset)r["timestamp"]!).ToList(), AilJson.Options);
        });
        app.MapGet("/api/v1/series", (string? metric, string? filters) =>
        {
            var ws = state.Ws(); metric ??= "failure_rate";
            if (!ws.Metrics.Contains(metric)) return Detail(404, $"unknown metric '{metric}'");
            return Results.Json(Explore.SeriesPayload(ws, metric, ParseFilters(filters)), AilJson.Options);
        });
        app.MapGet("/api/v1/cohorts", (string? metric, string? filters, int? window) =>
        {
            var ws = state.Ws(); metric ??= "failure_rate";
            if (!ws.Metrics.Contains(metric)) return Detail(404, $"unknown metric '{metric}'");
            if (window is < 1 or > 1440) return Detail(422, "window must be between 1 and 1440");
            return Results.Json(Explore.Breakdown(ws, metric, ParseFilters(filters), window), AilJson.Options);
        });
        app.MapGet("/api/v1/changes", () => Results.Json(store.ListChanges(), AilJson.Options));
        app.MapGet("/api/v1/query", () => Detail(501, "natural-language queries are only available in the Python edition"));
        app.MapPost("/api/v1/analyze", (HttpContext c) =>
        {
            if (!TokenOk(c, options.ApiToken)) return Detail(401, "missing or invalid API token");
            processor.Drain();
            var found = Workflow.RunAnalysis(store, new DetectionConfig(), LlmProviders.Create());
            return Results.Json(new Dictionary<string, object> { ["incidents"] = found.Select(i => i.Id).ToList() }, AilJson.Options);
        });

        var portal = Path.Combine(ScenarioLoader.RepoRoot(), "apps", "portal");
        if (Directory.Exists(portal))
        {
            var files = new PhysicalFileProvider(portal);
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
        }
        app.Services.GetRequiredService<IHostApplicationLifetime>();
        return app;
    }

    /// <summary>The store behind a running app (for hosts that need to read results directly).</summary>
    public static TelemetryStore OpenStore(string path) => new(path);
}
